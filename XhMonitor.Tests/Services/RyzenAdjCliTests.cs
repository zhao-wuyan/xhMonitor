using FluentAssertions;
using XhMonitor.Core.Models;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Services;

public class RyzenAdjCliTests
{
    [Fact]
    public void DoneWhen_TryParseInfoOutput_ParsesRequiredPowerFields()
    {
        var output = """
CPU Family: Renoir
SMU BIOS Interface Version: 123
Version: v0.0.0
PM Table Version: 0000
|        Name         |   Value   |     Parameter      |
|---------------------|-----------|--------------------|
| STAPM LIMIT         | 55000.000 | stapm-limit        |
| STAPM VALUE         | 42000.000 |                    |
| PPT LIMIT FAST      | 100000.000 | fast-limit        |
| PPT VALUE FAST      | 80000.000 |                    |
| PPT LIMIT SLOW      | 55000.000 | slow-limit         |
| PPT VALUE SLOW      | 45000.000 |                    |
""";

        var ok = RyzenAdjCli.TryParseInfoOutput(output, out var snapshot, out var error);

        ok.Should().BeTrue(error);
        snapshot.Should().Be(new RyzenAdjSnapshot(
            StapmLimit: 55000.0,
            StapmValue: 42000.0,
            FastLimit: 100000.0,
            FastValue: 80000.0,
            SlowLimit: 55000.0,
            SlowValue: 45000.0));
    }
}

