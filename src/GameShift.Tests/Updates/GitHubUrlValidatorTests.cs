using GameShift.Core.Updates;
using Xunit;

namespace GameShift.Tests.Updates;

public class GitHubUrlValidatorTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/releases/download/v1.0/file.exe")]
    [InlineData("https://api.github.com/repos/owner/repo/releases/latest")]
    [InlineData("https://objects.githubusercontent.com/something")]
    [InlineData("https://raw.githubusercontent.com/owner/repo/main/file.txt")]
    public void ValidGitHubUrls_ReturnTrue(string url)
    {
        Assert.True(GitHubUrlValidator.IsValid(url));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]  // HTTP not HTTPS
    [InlineData("ftp://github.com/file")]
    public void NonHttpsUrls_ReturnFalse(string url)
    {
        Assert.False(GitHubUrlValidator.IsValid(url));
    }

    [Theory]
    [InlineData("https://fake-github.com/evil")]
    [InlineData("https://github.com.evil.net/evil")]
    [InlineData("https://githubusercontent.com.evil/evil")]
    [InlineData("https://evil.com/github.com/trick")]
    public void TyposquatDomains_ReturnFalse(string url)
    {
        Assert.False(GitHubUrlValidator.IsValid(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/evil.exe")]
    public void InvalidOrMalformedInput_ReturnFalse(string? url)
    {
        Assert.False(GitHubUrlValidator.IsValid(url));
    }

    [Fact]
    public void HostCaseInsensitive()
    {
        Assert.True(GitHubUrlValidator.IsValid("https://GITHUB.com/path"));
        Assert.True(GitHubUrlValidator.IsValid("https://GitHub.COM/path"));
    }
}
