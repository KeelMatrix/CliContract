using KeelMatrix.Telemetry;

internal interface IActivationSink
{
    void TrackActivation();
}

internal static class ActivationTelemetry
{
    private static readonly object Sync = new();
    private static Action? testActivation;

    internal static IDisposable UseTestSink(IActivationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Sync)
        {
            if (testActivation is not null)
            {
                throw new InvalidOperationException("An activation test sink is already installed.");
            }

            testActivation = sink.TrackActivation;
            return new TestSinkScope();
        }
    }

    internal static void TrackActivation()
    {
        Action? test;
        lock (Sync)
        {
            test = testActivation;
        }

        if (test is not null)
        {
            test();
            return;
        }

        new Client("devtool", typeof(CliApplication)).TrackActivation();
    }

    private sealed class TestSinkScope : IDisposable
    {
        public void Dispose()
        {
            lock (Sync)
            {
                testActivation = null;
            }
        }
    }
}
