using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HitCam.Desktop.Services;
using HitCam.Desktop.Views;

namespace HitCam.Desktop.Tests;

public sealed class FeatureSectionTests
{
    [Fact]
    public void Sections_start_collapsed_and_are_remembered()
    {
        Assert.Equal(new ExperimentSections(), AppSettings.Parse("""{"serverId":"a"}"""u8)!.Sections);
        Assert.True(AppSettings.Parse("""{"sections":{"faces":true}}"""u8)!.Sections.Faces);
        Assert.Equal(new ExperimentSections(), AppSettings.Parse("""{"sections":null}"""u8)!.Sections);
    }

    [Fact]
    public void The_address_is_not_on_the_streaming_screen()
    {
        Assert.DoesNotContain(".", Loc.DeviceSubtitle.Replace("Wi-Fi", ""));
    }

    [Fact]
    public async Task Settings_show_when_expanded_and_grey_out_while_off()
    {
        await ShotOverlayTests.Session.Value.Dispatch(() =>
        {
            // The headless app has no theme and none of the app's resources: the window gets them before its content,
            // since a control picks its theme when it is attached.
            var window = new Window { Width = 400, Height = 300 };
            window.Styles.Add(new FluentTheme());
            window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://HitCam/"))
            {
                Source = new Uri("avares://HitCam/Views/FeatureSection.axaml"),
            });
            var content = new TextBlock { Text = "settings" };
            var section = new FeatureSection { Title = "Faces", Hint = "hint", Content = content };
            // In the app the theme comes from App.axaml; here it is set directly.
            section.Theme = (Avalonia.Styling.ControlTheme)window.FindResource(typeof(FeatureSection))!;
            window.Content = section;
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The settings' holder in the template: hidden while collapsed, disabled (grey) while off.
            var holder = section.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter" && p.TemplatedParent == section);
            Assert.False(holder.IsVisible);
            section.IsExpanded = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(holder.IsVisible);
            Assert.False(content.IsEffectivelyEnabled);

            section.IsOn = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(content.IsEffectivelyEnabled);

            // A click on the title collapses (anywhere on the header does); a click on the switch only switches.
            Click(window, new Avalonia.Point(20, 10));
            Assert.False(section.IsExpanded);
            Click(window, new Avalonia.Point(20, 10));
            Assert.True(section.IsExpanded);
            var toggle = section.GetVisualDescendants().OfType<ToggleSwitch>().Single();
            var center = toggle.TranslatePoint(new Avalonia.Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            Click(window, center);
            Assert.True(section.IsExpanded);
            Assert.False(section.IsOn);
            Assert.NotNull(window.CaptureRenderedFrame());
            window.Close();
        }, CancellationToken.None);
    }

    private static void Click(Window window, Avalonia.Point point)
    {
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        // Two clicks in a row must not be taken for a double click.
        Thread.Sleep(600);
    }
}
