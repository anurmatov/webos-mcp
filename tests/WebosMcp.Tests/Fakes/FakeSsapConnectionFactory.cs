using System.Net;
using WebosMcp.Application;

namespace WebosMcp.Tests.Fakes;

public sealed class FakeSsapConnectionFactory : ISsapConnectionFactory
{
    private readonly Queue<FakeSsapConnection> _queued = new();

    public List<FakeSsapConnection> Created { get; } = [];

    public int CreateCount => Created.Count;

    /// <summary>Connections handed out in order; the last one repeats once exhausted.</summary>
    public FakeSsapConnectionFactory Enqueue(params FakeSsapConnection[] connections)
    {
        foreach (var connection in connections)
        {
            _queued.Enqueue(connection);
        }

        return this;
    }

    public ISsapConnection Create(IPEndPoint endpoint, bool useTls)
    {
        var connection = _queued.Count > 0 ? _queued.Dequeue() : new FakeSsapConnection();
        Created.Add(connection);
        return connection;
    }
}
