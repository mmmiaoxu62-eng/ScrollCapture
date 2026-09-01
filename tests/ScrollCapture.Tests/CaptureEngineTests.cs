using ScrollCapture.Capture;

namespace ScrollCapture.Tests;

public class CaptureEngineTests
{
    private static byte[] Uniform(int length, byte value)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static byte[] Varied(int length, int seed)
    {
        var buffer = new byte[length];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    [Fact]
    public void IsBlank_UniformBlackAndWhite_True()
    {
        Assert.True(CaptureEngine.IsBlankContent(Uniform(4096, 0)));      // pure black
        Assert.True(CaptureEngine.IsBlankContent(Uniform(4096, 255)));    // pure white
    }

    [Fact]
    public void IsBlank_RealContent_False()
    {
        Assert.False(CaptureEngine.IsBlankContent(Varied(4096, seed: 42)));
    }

    [Fact]
    public void IsBlank_EdgeCases()
    {
        Assert.True(CaptureEngine.IsBlankContent(Array.Empty<byte>()));
        Assert.True(CaptureEngine.IsBlankContent(Uniform(100, 128))); // flat mid gray
    }
}
