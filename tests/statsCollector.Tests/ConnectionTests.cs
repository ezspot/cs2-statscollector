using System.Text.RegularExpressions;
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

    // The pipeline's timeout only asks MySqlConnector to cancel (KILL QUERY over a new connection): against a server
    // that stopped answering, the query ran on until DefaultCommandTimeout + CancellationTimeout (30 s + 2 s).
    [Fact]
    public void HungQueryEndsASecondAfterThePipelineAttemptTimeout()
    {
        var builder = new MySqlConnectionStringBuilder(new PluginConfig().BuildConnectionString());

        Assert.Equal(ResiliencePolicies.AttemptTimeout, TimeSpan.FromSeconds(builder.DefaultCommandTimeout));
        Assert.Equal(1, builder.CancellationTimeout);
    }

    // DefaultCommandTimeout does not reach a bulk copy: its LOAD DATA runs with CommandTimeout = BulkCopyTimeout,
    // which is 0, no timeout, unless the bulk copy sets it.
    [Fact]
    public void EveryBulkCopyEndsWithinThePipelineAttemptTimeout()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "statsCollector.csproj"))) directory = directory.Parent;

        var copies = Directory.EnumerateFiles(Path.Combine(directory.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"new MySqlBulkCopy\([^;]*;").Select(m => $"{Path.GetFileName(file)}: {m.Value}"))
            .ToList();

        Assert.NotEmpty(copies);
        Assert.All(copies, copy => Assert.Contains("BulkCopyTimeout = (int)ResiliencePolicies.AttemptTimeout.TotalSeconds", copy));
    }

    // Under Required (or Preferred) MySqlConnector accepts any server certificate and never checks it against the CA.
    [Fact]
    public void SslCaIsRejectedUnlessTheModeVerifiesIt()
    {
        var ca = Path.GetTempFileName();
        try
        {
            var config = new PluginConfig { DatabasePassword = "password", DatabaseSslCa = ca, DatabaseSslMode = "Required" };
            Assert.Throws<ArgumentException>(config.Validate);

            config.DatabaseSslMode = "VerifyCA";
            config.Validate();
        }
        finally
        {
            File.Delete(ca);
        }
    }
}
