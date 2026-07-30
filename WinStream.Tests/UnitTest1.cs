using WinStream.Core;

namespace WinStream.Tests;

public class ProductIdentityTests
{
    [Fact]
    public void ProductNameIsStable()
    {
        Assert.Equal("WinStream", ProductIdentity.Name);
        Assert.False(string.IsNullOrWhiteSpace(ProductIdentity.SingleInstanceKey));
    }
}
