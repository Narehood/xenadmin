using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class InfrastructureRenderingFixture : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(InfrastructureRenderingFixture));

    public Task Run(Action test) => _session.Dispatch(test, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    // Use the public session API: Avalonia 12.1.3's xUnit adapter targets the older
    // xUnit 3.2.2 discovery ABI. Keep real icon decoding and framebuffer pixels.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
