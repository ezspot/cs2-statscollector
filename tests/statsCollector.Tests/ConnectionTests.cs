using MySqlConnector;
using statsCollector.Config;
using statsCollector.Infrastructure;
using Xunit;

namespace statsCollector.Tests;

public class ConnectionTests
{
    // OpenAsync ignores cancellation while it waits for the server's greeting: against a server that accepts and never
    // answers, only ConnectionTimeout ends the attempt (15 s by default, three times the pipeline's attempt budget).
    [Fact]
    public void ConnectAttemptEndsWithinThePipelineAttemptTimeout()
    {
        var builder = new MySqlConnectionStringBuilder(new PluginConfig().BuildConnectionString());

        Assert.Equal(ResiliencePolicies.AttemptTimeout, TimeSpan.FromSeconds(builder.ConnectionTimeout));
    }
}
