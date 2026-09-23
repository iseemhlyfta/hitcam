using ReactiveUI.Builder;

[assembly: AssemblyFixture(typeof(HitCam.Desktop.Tests.ReactiveUiFixture))]

namespace HitCam.Desktop.Tests;

/// <summary>ReactiveUI must be initialized once per process, as Program.UseReactiveUI does for the app.</summary>
public sealed class ReactiveUiFixture
{
    public ReactiveUiFixture() =>
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
}
