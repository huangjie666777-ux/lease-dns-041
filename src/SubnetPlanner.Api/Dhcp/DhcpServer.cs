using System.Net;
using System.Net.Sockets;

namespace SubnetPlanner.Api.Dhcp;

public sealed class DhcpServer : BackgroundService
{
    private readonly DhcpMessageHandler _handler;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DhcpServer> _logger;

    public DhcpServer(DhcpPool pool, IConfiguration configuration, ILogger<DhcpServer> logger)
    {
        _handler = new DhcpMessageHandler(pool);
        _configuration = configuration;
        _logger = logger;
    }

    public int BoundPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _configuration.GetValue<int?>("Dhcp:Port") ?? 6767;
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException ex)
        {
            _logger.LogWarning("DHCP端口 {Port} 绑定失败，服务未启动: {Message}", port, ex.Message);
            return;
        }
        using (udp)
        {
        BoundPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _logger.LogInformation("DHCP服务已监听 127.0.0.1:{Port}", BoundPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("DHCP接收失败: {Message}", ex.Message);
                continue;
            }

            byte[]? reply;
            try
            {
                reply = _handler.Process(received.Buffer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("畸形DHCP报文已忽略: {Message}", ex.Message);
                continue;
            }
            if (reply is null) continue;
            try
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, stoppingToken);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                _logger.LogWarning("DHCP回复发送失败: {Message}", ex.Message);
            }
        }
        }
    }
}
