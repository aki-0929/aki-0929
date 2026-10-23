# Kaling.IO

`CommandChannel` 在一个 `IDuplexPipe`（Kestrel 连接、Socket、命名管道等）上双向收发命令对象：

- 每个命令序列化后按 `MaxFragmentSize` 切成分片发送。多个命令的分片在线路上交错，所以一个几十 MB 的命令正在发送时，后面的小命令仍然能立即送达。
- 提供 `PingAsync` 测往返时间，`CloseAsync` 优雅关闭（两端交换 GoAway，已开始的发送会发完）。
- 连接内部不启动后台线程或定时器。对端的 Ping、PingAck 和关闭握手都在 `ReadAsync` 里处理，所以必须有代码持续调用 `ReadAsync`（见第 4 节）。

内存 loopback 压测见 WPF 项目 `Kaling.StressTest`（`dotnet run --project Kaling.StressTest`，窗口内实时大屏刷新，点「停止压测」结束）。

## 1. 定义命令

继承 `Command`，自己负责 payload 的序列化；分帧由连接处理，payload 大小不限。

```csharp
public sealed class ChatCommand : Command
{
    public string Text { get; set; } = "";

    public override void Serialize(IBufferWriter<byte> writer)
        => writer.Write(Encoding.UTF8.GetBytes(Text));

    public override void Deserialize(in ReadOnlySequence<byte> payload)
        => Text = Encoding.UTF8.GetString(payload);
}
```

`Deserialize` 收到的 `payload` 只在本次调用内有效，需要保留的数据要自己拷贝出来（例如 `payload.ToArray()`）。命令类型必须有无参构造函数。

## 2. 注册命令

`CommandRegistry` 把命令类型映射到线路上的 type id。**两端必须使用相同的映射**；registry 不可变，可以被所有连接共享。

```csharp
public static readonly CommandRegistry Registry = new(b => b
    .Register<LoginCommand>(1)
    .Register<ChatCommand>(2)
    .Register<BlobCommand>(3));
```

## 3. 创建连接

```csharp
var connection = new CommandChannel(transport, Registry);           // transport: IDuplexPipe
var connection = new CommandChannel(transport, Registry, options);  // 自定义本地限制
```

不需要握手，创建后即可读写。连接结束时会 Complete 传输管道，但**不会 Dispose 传输本身**，传输的释放由调用方负责。

Kestrel 服务端可以直接用 `ConnectionContext.Transport`：

```csharp
internal sealed class CommandConnectionHandler : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext context)
    {
        var connection = new CommandChannel(context.Transport, Registry);
        // ... 读循环，见下文
    }
}

// builder.WebHost.ConfigureKestrel(k =>
//     k.ListenLocalhost(5070, l => l.UseConnectionHandler<CommandConnectionHandler>()));
```

### 3.1 `IDuplexPipe` 的几种实现

通道只要求传输是一条**可靠、有序的字节流**，所以 TCP、命名管道、TLS、WebSocket、QUIC 流、KCP 都可以用，通道本身不关心下面是什么。除了 Kestrel 的 `Transport`，其他传输都先拿到一个 `Stream`，再包成 `IDuplexPipe`。下面这个小类就够用了：

```csharp
public sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;

    public PipeWriter Output { get; } = output;

    // leaveOpen: true 很重要：通道结束时会 Complete 管道，
    // 不加的话会把 stream 一起关掉，而传输的释放应该由你来做。
    public static DuplexPipe FromStream(Stream stream) => new(
        PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)),
        PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));
}
```

**TCP 客户端**

```csharp
using var client = new TcpClient { NoDelay = true };
await client.ConnectAsync(host, port);
var channel = new CommandChannel(DuplexPipe.FromStream(client.GetStream()), Registry);
```

**命名管道**

```csharp
await using var stream = new NamedPipeClientStream(".", "my-pipe", PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
await stream.ConnectAsync();
var channel = new CommandChannel(DuplexPipe.FromStream(stream), Registry);
```

**TLS**（在 TCP 之上再包一层 `SslStream`）

```csharp
using var client = new TcpClient { NoDelay = true };
await client.ConnectAsync(host, port);
await using var ssl = new SslStream(client.GetStream());
await ssl.AuthenticateAsClientAsync(host);
var channel = new CommandChannel(DuplexPipe.FromStream(ssl), Registry);
```

**WebSocket**（用 `WebSocketStream` 把消息流转成字节流，需要 .NET 9 及以上）

```csharp
using var ws = new ClientWebSocket();
await ws.ConnectAsync(uri, cancellationToken);
Stream stream = WebSocketStream.Create(ws, WebSocketMessageType.Binary);
var channel = new CommandChannel(DuplexPipe.FromStream(stream), Registry);
```

**QUIC**（一个 `QuicStream` 就是一条字节流；通道只用这一条流，不会利用 QUIC 的多流隔离）

```csharp
await using QuicStream stream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
var channel = new CommandChannel(DuplexPipe.FromStream(stream), Registry);
```

**KCP**（UDP 上的可靠传输，用 NuGet 包 [KcpSharp](https://www.nuget.org/packages/KcpSharp)：`dotnet add package KcpSharp`）

```csharp
// 通道需要字节流，所以必须用流模式；默认的消息模式不能用。
// 其他参数也必须调：KcpSharp 的默认值很保守，实测本机回环上 Ping 约 300 ms，
// 4 MiB 的命令 60 秒都发不完；下面这组参数下 Ping 约 3 ms，4 MiB 约 0.6 秒。
var options = new KcpConversationOptions
{
    StreamMode = true,
    NoDelay = true,
    UpdateInterval = 10,
    FastResend = 2,
    SendWindow = 256,
    ReceiveWindow = 256,
    SendQueueSize = 512,
    ReceiveQueueSize = 512,
};

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
socket.Bind(localEndPoint);
await socket.ConnectAsync(remoteEndPoint);       // 两端各绑定自己的地址，再连接对方的地址

// 1 是会话编号，两端必须一致
using var transport = KcpSocketTransport.CreateConversation(socket, remoteEndPoint, 1, options)!;
transport.Start();

using var stream = new KcpStream(transport.Connection, false);   // false：流不负责释放会话
var channel = new CommandChannel(DuplexPipe.FromStream(stream), Registry);
```

使用 KCP 前请注意：

- **只有点对点。** KCP 没有握手，KcpSharp 也没有监听和接受连接的机制：两端的地址和会话编号要事先约定好。一个服务端要服务多个客户端时，要自己按客户端地址分别建套接字和会话。
- **没有加密。** 需要的话要在上层自己做。
- **这个库不够成熟。** 版本是 0.8.8，2022 年之后没有更新，.NET 里目前没有更成熟的 KCP 实现。上线前请自己测一遍延迟、吞吐和丢包下的表现。
- 释放时，先结束通道（读循环退出、`Abort()` 或 `CloseAsync`），再释放 `stream`、`transport` 和 `socket`。

**内存中的一对管道**（单元测试或同进程通信）

```csharp
var aToB = new Pipe();
var bToA = new Pipe();
var a = new CommandChannel(new DuplexPipe(bToA.Reader, aToB.Writer), Registry);
var b = new CommandChannel(new DuplexPipe(aToB.Reader, bToA.Writer), Registry);
```

其他传输只要能提供一条可靠有序的 `Stream`，用法完全一样。注意不要把裸 UDP 之类的不可靠传输直接接进来：通道靠字节顺序解析分片，丢包或乱序后的数据没法正确解析，最好的情况是抛 `CommandChannelProtocolException`，也可能读出错误的命令。

## 4. 读循环（必须）

同一时间只能有一个 `ReadAsync` 在执行，并且**必须持续读取**：对端的 Ping、PingAck 和关闭握手都是在 `ReadAsync` 内部处理的，不读的话 Ping 和 `CloseAsync` 都无法完成。

```csharp
try
{
    while (true)
    {
        switch (await connection.ReadAsync(cancellationToken))
        {
            case LoginCommand login:
                // ...
                break;
            case BlobCommand blob:
                // 耗时操作放到别处执行，别卡住读循环
                _ = Task.Run(() => Handle(blob));
                break;
        }
    }
}
catch (CommandChannelClosedException ex)
{
    // 优雅关闭：ex.CloseInitiator 是 Local/Remote，ex.Reason 是关闭原因
}
catch (CommandChannelProtocolException ex)
{
    // 对端违反协议：ex.ErrorCode；连接已被 Abort
}
catch (CommandChannelAbortedException ex)
{
    // 异常终止：ex.InnerException 是根本原因（超时、传输断开、Abort 等）
}
```

`ReadAsync` 不会返回 `null`。优雅关闭时，对端在关闭前发出的命令都会先被读出来，之后才抛出 `CommandChannelClosedException`。

## 5. 发送命令

```csharp
await connection.SendAsync(new ChatCommand { Text = "hello" });
```

- 线程安全，可以并发调用。并发发送的命令在线路上交错，**对端按发送完成的先后收到，而不是调用的先后**。需要保持顺序时，等上一个 `SendAsync` 完成再发下一个。
- 超过 `MaxFragmentSize` 的命令会被拆成多个分片；同时在发的多分片命令数受 `MaxConcurrentFragmentedCommands` 限制，超出的发送者会排队等待。
- `cancellationToken` 在等待和分片之间生效；取消一个只发出了一半的命令时，会通知对端丢弃已收到的部分，连接仍然可用。
- 正在写入传输的那一个分片不会被 `cancellationToken` 打断，否则线路数据会错乱。如果对端一直不读，这次写入会一直等下去，只有 `Abort()` 能结束它。需要限时的话，由调用方自己计时，超时后调用 `Abort()`。
- 抛出 `CommandChannelClosedException` 表示命令**完全没有发出去**，可以放心换一个连接重发。

## 6. Ping

```csharp
TimeSpan rtt = await connection.PingAsync(cancellationToken);
```

返回往返时间。取消后连接仍可继续使用（迟到的 PingAck 会被丢弃）。对端不回应时，这次调用会一直等，由调用方的 `cancellationToken` 或 `Abort()` 结束它。

## 7. 关闭

```csharp
// 优雅关闭：停止新的发送，等已经开始的发送发完，再和对端交换 GoAway；两边都结束后才返回
await connection.CloseAsync("server shutting down", cancellationToken);

// 立即终止：原因不会发给对端
connection.Abort();
```

- `CloseAsync` 的 `cancellationToken` 先触发时，连接会被 Abort。
- 关闭期间读循环必须继续运行，否则握手无法完成。
- 关闭原因的 UTF-8 编码不能超过 65535 字节。
- 优雅关闭后，两端的 `ReadAsync` 都会抛出 `CommandChannelClosedException`，`Reason` 就是发起方传入的原因。

## 8. 配置项 `CommandChannelOptions`

所有配置都只作用于本端，不会和对端协商。

| 属性 | 默认值 | 作用 |
| --- | --- | --- |
| `MaxFragmentSize` | 16 KiB | 本端发送的分片 payload 大小，范围 1024 ~ 65535 |
| `MaxConcurrentFragmentedCommands` | 100 | 本端同时在发的多分片命令数上限 |

```csharp
var options = new CommandChannelOptions
{
    MaxFragmentSize = 32 * 1024,
};
```

## 9. 异常一览

| 异常 | 含义 |
| --- | --- |
| `CommandChannelClosedException` | 连接正在关闭或已经优雅关闭；没发出去的命令可以重发 |
| `CommandChannelAbortedException` | 连接异常终止，`InnerException` 是根本原因 |
| `CommandChannelProtocolException` | 对端违反协议，只由检测到问题的那次 `ReadAsync` 抛出，`ErrorCode` 说明具体原因 |
| `ArgumentException` | `SendAsync` 传入了未注册的命令类型 |
| `InvalidOperationException` | 同时调用了多个 `ReadAsync` |

`CommandChannelClosedException` 和 `CommandChannelAbortedException` 都继承自 `OperationCanceledException`，只关心「连接没了」时可以统一 catch `OperationCanceledException`。
