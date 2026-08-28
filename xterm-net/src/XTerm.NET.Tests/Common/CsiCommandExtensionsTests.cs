using XTerm.Common;

namespace XTerm.Tests.Common;

public class CsiCommandExtensionsTests
{
    [Theory]
    [InlineData("?S", CsiCommand.GraphicsAttributes)]
    [InlineData("S", CsiCommand.ScrollUp)]
    [InlineData("?h", CsiCommand.SetMode)]
    [InlineData("?l", CsiCommand.ResetMode)]
    [InlineData("?c", CsiCommand.DeviceAttributes)]
    [InlineData(">c", CsiCommand.DeviceAttributes)]
    [InlineData("?n", CsiCommand.DeviceStatusReport)]
    [InlineData("?$p", CsiCommand.RequestMode)]
    public void ToCsiCommand_MapsExplicitPrivateIdentifiers(string identifier, CsiCommand command)
    {
        Assert.Equal(command, identifier.ToCsiCommand());
    }

    [Theory]
    [InlineData("?u")]
    [InlineData(">q")]
    public void ToCsiCommand_UnmappedPrivateIdentifiers_ReturnUnknown(string identifier)
    {
        Assert.Equal(CsiCommand.Unknown, identifier.ToCsiCommand());
    }
}
