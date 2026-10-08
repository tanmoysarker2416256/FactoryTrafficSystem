using System.Threading.Channels;
using TrafficControl.Application;
using TrafficControl.Domain;

namespace TrafficControl.Infrastructure;

/// <summary>
/// REST-simulated physical controller. It implements the same port an MQTT adapter would implement.
/// SendAsync just drops the command into an outbox. A background service (in the API project) reads the outbox and,
/// when AutoAck is on, answers with an ACK. Switching AutoAck off lets you demonstrate "ACK never received".
/// </summary>
public sealed class SimulatedController : IControllerGateway
{
    private readonly Channel<SendControllerCommand> _outbox = Channel.CreateUnbounded<SendControllerCommand>();
    private volatile bool _autoAck = true;

    public bool AutoAck
    {
        get => _autoAck;
        set => _autoAck = value;
    }

    public TimeSpan AckDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public ChannelReader<SendControllerCommand> Reader => _outbox.Reader;

    public Task SendAsync(SendControllerCommand command, CancellationToken ct = default)
    {
        _outbox.Writer.TryWrite(command);
        return Task.CompletedTask;
    }
}
