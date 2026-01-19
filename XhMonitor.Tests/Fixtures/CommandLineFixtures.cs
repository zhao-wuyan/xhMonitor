namespace XhMonitor.Tests.Fixtures;

/// <summary>
/// Test data for ProcessNameResolver
/// </summary>
public class CommandLineTestCase
{
    public required string ProcessName { get; init; }
    public required string[] Keywords { get; init; }
    public required string CommandLine { get; init; }
    public required string ExpectedDisplayName { get; init; }
    public string? Description { get; init; }
}

public static class CommandLineFixtures
{
    public static IEnumerable<object[]> GetTestCases()
    {
        return AllTestCases.Select(tc => new object[] { tc });
    }

    public static readonly List<CommandLineTestCase> AllTestCases = new()
    {
        // Python + FastAPI scenarios
        new CommandLineTestCase
        {
            ProcessName = "python",
            Keywords = new[] { "fastapi", "uvicorn" },
            CommandLine = "python -m uvicorn main:app --host 0.0.0.0 --port 8000",
            ExpectedDisplayName = "Python: FastAPI",
            Description = "Python FastAPI with uvicorn"
        },
        new CommandLineTestCase
        {
            ProcessName = "python",
            Keywords = new[] { "fastapi", "uvicorn" },
            CommandLine = "python app.py --fastapi-mode",
            ExpectedDisplayName = "Python: FastAPI",
            Description = "Python FastAPI custom script"
        },

        // Python + Django scenarios
        new CommandLineTestCase
        {
            ProcessName = "python",
            Keywords = new[] { "django", "manage.py" },
            CommandLine = "python manage.py runserver 0.0.0.0:8000",
            ExpectedDisplayName = "Python: Django",
            Description = "Python Django runserver"
        },
        new CommandLineTestCase
        {
            ProcessName = "python",
            Keywords = new[] { "django", "manage.py" },
            CommandLine = "python manage.py migrate",
            ExpectedDisplayName = "Python: Django",
            Description = "Python Django migrate"
        },

        // Python + ComfyUI scenarios
        new CommandLineTestCase
        {
            ProcessName = "python",
            Keywords = new[] { "comfyui", "main.py" },
            CommandLine = "python main.py --listen 0.0.0.0 --port 8188",
            ExpectedDisplayName = "Python: ComfyUI",
            Description = "Python ComfyUI main.py"
        },

        // Chrome scenarios (Direct type)
        new CommandLineTestCase
        {
            ProcessName = "chrome",
            Keywords = Array.Empty<string>(),
            CommandLine = "chrome.exe --profile-directory=Default",
            ExpectedDisplayName = "Google Chrome",
            Description = "Chrome Direct type"
        },

        // Edge cases - No matching rule
        new CommandLineTestCase
        {
            ProcessName = "unknown-process",
            Keywords = Array.Empty<string>(),
            CommandLine = "unknown-process --some-args",
            ExpectedDisplayName = "unknown-process",
            Description = "Process with no matching rule"
        },

        // Case sensitivity tests
        new CommandLineTestCase
        {
            ProcessName = "Python",
            Keywords = new[] { "FASTAPI" },
            CommandLine = "Python -m UVICORN main:app",
            ExpectedDisplayName = "Python: FastAPI",
            Description = "Case insensitive matching"
        },

        // Multiple keywords match
        new CommandLineTestCase
        {
            ProcessName = "python",
            Keywords = new[] { "fastapi", "uvicorn" },
            CommandLine = "python -m fastapi run --uvicorn",
            ExpectedDisplayName = "Python: FastAPI",
            Description = "Multiple keywords present"
        },

        // Chrome with complex arguments
        new CommandLineTestCase
        {
            ProcessName = "chrome",
            Keywords = Array.Empty<string>(),
            CommandLine = "chrome.exe --profile-directory=Default --app=https://example.com --window-size=1920,1080",
            ExpectedDisplayName = "Google Chrome",
            Description = "Chrome with multiple arguments"
        }
    };
}
