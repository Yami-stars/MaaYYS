using System.Text.Json;
using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Logging;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 应用级设置（P6.2）：默认值合法、非法取值夹回、深拷贝隔离、落盘往返。
/// </summary>
public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_AreValidAndRoundTripThroughSanitize()
    {
        var settings = new AppSettings();
        string before = JsonSerializer.Serialize(settings);

        settings.Sanitized();

        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal("FramePool", settings.Connection.ScreencapMethod);
        Assert.Equal("SendMessage", settings.Connection.InputMethod);
        Assert.Equal(60, settings.Run.StallTimeoutSeconds);
        Assert.Equal(20, settings.Run.TimerPollSeconds);
        Assert.True(settings.Run.StopOnFailure);
        Assert.Equal("none", settings.Run.AfterCompletion);
        Assert.False(settings.FileLog.Enabled);

        // P6.3：通知设置默认值
        Assert.Equal(0, settings.Notification.StuckTimeoutSeconds);
        Assert.Equal(0, settings.Notification.ReminderIntervalMinutes);
        Assert.False(settings.Notification.ExternalEnabled);
        Assert.True(settings.Notification.OnComplete);
        Assert.True(settings.Notification.OnFailure);
        Assert.True(settings.Notification.OnStuck);
        Assert.Equal("POST", settings.Notification.WebhookMethod);
        Assert.Equal("application/json", settings.Notification.WebhookContentType);
        Assert.False(settings.Notification.ExternalReady());
    }

    [Fact]
    public void NotificationSanitize_ClampsRangesAndNormalizesMethod()
    {
        var settings = new AppSettings
        {
            Notification = new NotificationSettings
            {
                StuckTimeoutSeconds = 99999,
                ReminderIntervalMinutes = -10,
                WebhookMethod = "delete",
                WebhookContentType = "   ",
            },
        };

        settings.Sanitized();

        Assert.Equal(3600, settings.Notification.StuckTimeoutSeconds);
        Assert.Equal(0, settings.Notification.ReminderIntervalMinutes);
        Assert.Equal("POST", settings.Notification.WebhookMethod); // 未知方法回落 POST
        Assert.Equal("application/json", settings.Notification.WebhookContentType); // 空 Content-Type 回落默认
    }

    [Fact]
    public void NotificationExternalReady_RequiresEnabledUrlAndTrigger()
    {
        var off = new NotificationSettings { ExternalEnabled = false, WebhookUrl = "http://x", OnComplete = true };
        Assert.False(off.ExternalReady());

        var noUrl = new NotificationSettings { ExternalEnabled = true, WebhookUrl = "", OnComplete = true };
        Assert.False(noUrl.ExternalReady());

        var noTrigger = new NotificationSettings { ExternalEnabled = true, WebhookUrl = "http://x", OnComplete = false, OnFailure = false, OnStuck = false };
        Assert.False(noTrigger.ExternalReady());

        var ready = new NotificationSettings { ExternalEnabled = true, WebhookUrl = "http://x", OnComplete = true };
        Assert.True(ready.ExternalReady());
    }

    [Fact]
    public void Sanitize_ClampsOutOfRangeValues()
    {
        var settings = new AppSettings
        {
            Connection = new ConnectionSettings { PreviewFps = 999, Win32TitleRegex = "   " },
            Startup = new StartupSettings { PreProgramWaitSeconds = -5 },
            Run = new RunSettings
            {
                StallTimeoutSeconds = -1,
                MaxRetries = 99,
                RetryDelayMs = -100,
                TimerPollSeconds = 1,
                MaxDurationSeconds = 999_999,
            },
            FileLog = new FileLogSettings { MaxFileSizeMb = 0, MaxFiles = 0, MinLevel = (LogLevel)42 },
        };

        settings.Sanitized();

        Assert.Equal(30, settings.Connection.PreviewFps);
        Assert.Equal("阴阳师", settings.Connection.Win32TitleRegex);
        Assert.Equal(0, settings.Startup.PreProgramWaitSeconds);
        Assert.Equal(0, settings.Run.StallTimeoutSeconds);
        Assert.Equal(10, settings.Run.MaxRetries);
        Assert.Equal(0, settings.Run.RetryDelayMs);
        Assert.Equal(5, settings.Run.TimerPollSeconds);
        Assert.Equal(24 * 3600, settings.Run.MaxDurationSeconds);
        Assert.Equal(1, settings.FileLog.MaxFileSizeMb);
        Assert.Equal(1, settings.FileLog.MaxFiles);
        Assert.Equal(LogLevel.Info, settings.FileLog.MinLevel);
    }

    [Theory]
    [InlineData("FramePool", "FramePool")]
    [InlineData("framepool", "FramePool")]
    [InlineData("DXGI_DesktopDup", "DXGI_DesktopDup")]
    [InlineData("不存在的方式", "FramePool")]
    [InlineData("", "FramePool")]
    public void Sanitize_KeepsOnlyKnownMethods(string input, string expected)
    {
        var settings = new AppSettings { Connection = new ConnectionSettings { ScreencapMethod = input } };

        settings.Sanitized();

        Assert.Equal(expected, settings.Connection.ScreencapMethod);
    }

    [Theory]
    [InlineData("shutdown", "shutdown")]
    [InlineData("EXIT", "exit")]
    [InlineData("reboot", "none")]
    [InlineData(null, "none")]
    public void AfterCompletion_IsNormalizedToOneOfThree(string? input, string expected)
    {
        Assert.Equal(expected, RunSettings.Normalize(input));
    }

    [Fact]
    public void Clone_IsDeepAndCopyFromKeepsNestedInstances()
    {
        var original = new AppSettings();
        original.Run.MaxRetries = 3;
        original.Connection.PreviewFps = 10;

        AppSettings clone = original.Clone();
        clone.Run.MaxRetries = 7;

        Assert.Equal(3, original.Run.MaxRetries);
        Assert.NotSame(original.Run, clone.Run);

        // CopyFrom 不换子对象（服务持有的是这些引用）
        RunSettings runRef = original.Run;
        original.CopyFrom(clone);
        Assert.Same(runRef, original.Run);
        Assert.Equal(7, original.Run.MaxRetries);
    }

    [Fact]
    public void FileLog_ResolveDirectory_UsesBaseWhenRelativeOrEmpty()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "maayys-base");

        var defaults = new FileLogSettings();
        Assert.Equal(Path.Combine(baseDir, "logs"), defaults.ResolveDirectory(baseDir));

        var relative = new FileLogSettings { Directory = "var/log" };
        Assert.Equal(Path.Combine(baseDir, "var/log"), relative.ResolveDirectory(baseDir));

        string absolute = Path.Combine(Path.GetTempPath(), "abs-logs");
        var rooted = new FileLogSettings { Directory = absolute };
        Assert.Equal(absolute, rooted.ResolveDirectory(baseDir));
    }

    [Fact]
    public void Store_PersistsSettingsAcrossReload()
    {
        string dir = Path.Combine(Path.GetTempPath(), "maayys-settings-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "maayys.json");
        try
        {
            var store = new ConfigurationStore(path);
            store.Load();
            store.UpdateSettings(new AppSettings
            {
                Startup = new StartupSettings
                {
                    PreProgramPath = @"D:\Games\yys\yys.exe",
                    PreProgramArguments = "-windowed",
                    PreProgramWaitSeconds = 25,
                    AutoStartWithWindows = true,
                },
                Run = new RunSettings
                {
                    StallTimeoutSeconds = 120,
                    MaxRetries = 2,
                    StopOnFailure = false,
                    AfterCompletion = "shutdown",
                },
                FileLog = new FileLogSettings { Enabled = true, MaxFileSizeMb = 20, MinLevel = LogLevel.Warn },
            });

            var reloaded = new ConfigurationStore(path);
            reloaded.Load();

            Assert.Equal(@"D:\Games\yys\yys.exe", reloaded.Settings.Startup.PreProgramPath);
            Assert.Equal(25, reloaded.Settings.Startup.PreProgramWaitSeconds);
            Assert.True(reloaded.Settings.Startup.AutoStartWithWindows);
            Assert.Equal(120, reloaded.Settings.Run.StallTimeoutSeconds);
            Assert.Equal(2, reloaded.Settings.Run.MaxRetries);
            Assert.False(reloaded.Settings.Run.StopOnFailure);
            Assert.Equal("shutdown", reloaded.Settings.Run.AfterCompletion);
            Assert.True(reloaded.Settings.FileLog.Enabled);
            Assert.Equal(20, reloaded.Settings.FileLog.MaxFileSizeMb);
            Assert.Equal(LogLevel.Warn, reloaded.Settings.FileLog.MinLevel);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Store_RejectsIllegalValuesOnLoad_WithoutThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "maayys-bad-settings-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "maayys.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """
            {
              "config_version": 1,
              "current": "默认配置",
              "configurations": { "默认配置": { "name": "默认配置", "tasks": [] } },
              "settings": {
                "connection": { "screencap_method": "瞎写的" },
                "run": { "stall_timeout_seconds": -3, "after_completion": "reboot" }
              }
            }
            """);

            var store = new ConfigurationStore(path);
            store.Load();

            Assert.Equal("FramePool", store.Settings.Connection.ScreencapMethod);
            Assert.Equal(0, store.Settings.Run.StallTimeoutSeconds);
            Assert.Equal("none", store.Settings.Run.AfterCompletion);
            Assert.Contains(store.LoadErrors, e => e.Contains("设置里有非法取值"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void ExecutionOptions_MirrorRunSettings()
    {
        ExecutionOptions options = ExecutionOptions.FromRunSettings(new RunSettings
        {
            StallTimeoutSeconds = 90,
            MaxRetries = 2,
            RetryDelayMs = 2500,
            StopOnFailure = false,
            MaxDurationSeconds = 1800,
        });

        Assert.Equal(90, options.StallTimeoutSeconds);
        Assert.Equal(2, options.MaxRetries);
        Assert.Equal(2500, options.RetryDelayMs);
        Assert.False(options.StopOnFailure);
        Assert.Equal(1800, options.MaxDurationSeconds);
    }

    [Fact]
    public void ExecutionOptions_NullSettings_UseDefaults()
    {
        ExecutionOptions options = ExecutionOptions.FromRunSettings(null);

        Assert.Equal(ExecutionOptions.Default.StallTimeoutSeconds, options.StallTimeoutSeconds);
        Assert.True(options.StopOnFailure);
    }
}
