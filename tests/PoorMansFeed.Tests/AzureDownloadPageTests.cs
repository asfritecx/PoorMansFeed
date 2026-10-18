using System.Text;
using PoorMansFeed.Core.Azure;

namespace PoorMansFeed.Tests;

public sealed class AzureDownloadPageTests
{
    private const string GoodUrl =
        "https://download.microsoft.com/download/7/1/d/71d86715-5596-4529-9b13-da13a5de5b63/ServiceTags_Public_20261012.json";

    [Fact]
    public void Finds_The_File_In_The_Embedded_Details_When_Script_Follows_The_Json()
    {
        byte[] page = Bytes(
            "<html><body><script>window.__DLCDetails__={\"dlcDetailsView\":{\"error\":\"\",\"downloadTitle\":\"Azure IP Ranges and Service Tags \u2013 Public Cloud\"," +
            "\"downloadFile\":[{\"isPrimary\":\"False\",\"name\":\"ServiceTags_Public_20261012.json\",\"url\":\"" + GoodUrl + "\"," +
            "\"size\":\"4388432\",\"version\":\"2026.10.12\"}],\"other\":{\"x\":[1,2,{\"y\":\"}\"}]}}};" +
            "var later = {\"a\":1}; window.other = 5;</script><div>Not JSON at all</div></body></html>");

        AzurePublishedFile? file = AzureDownloadPage.TryFind(page);

        Assert.NotNull(file);
        Assert.Equal(new Uri(GoodUrl), file.Url);
        Assert.Equal("ServiceTags_Public_20261012.json", file.FileName);
        Assert.Equal(new DateOnly(2026, 10, 12), file.FileDate);
    }

    [Fact]
    public void Finds_The_File_In_The_Real_Page_Shape()
    {
        string page = RealPageShape();

        AzurePublishedFile? file = AzureDownloadPage.TryFind(Bytes(page));

        Assert.NotNull(file);
        Assert.Equal("ServiceTags_Public_20261012.json", file.FileName);
    }

    [Fact]
    public void Finds_The_File_In_The_Real_Page_When_Available()
    {
        string? path = Environment.GetEnvironmentVariable("AZURE_PAGE_SAMPLE");
        if (path is null || !File.Exists(path))
            return;

        AzurePublishedFile? file = AzureDownloadPage.TryFind(File.ReadAllBytes(path));

        Assert.NotNull(file);
        Assert.StartsWith("ServiceTags_Public_", file.FileName);
        Assert.Equal("download.microsoft.com", file.Url.Host);
    }

    [Fact]
    public void Falls_Back_To_The_Download_Button_When_There_Is_No_Embedded_Details()
    {
        byte[] page = Bytes($"<html><body><a href=\"{GoodUrl}\" download=\"\" class=\"dlcdetail__download-btn btn\">Download</a></body></html>");

        AzurePublishedFile? file = AzureDownloadPage.TryFind(page);

        Assert.NotNull(file);
        Assert.Equal(new Uri(GoodUrl), file.Url);
    }

    [Fact]
    public void Falls_Back_To_The_Download_Button_When_The_Embedded_Json_Is_Cut_Off()
    {
        byte[] page = Bytes($"<script>window.__DLCDetails__={{\"dlcDetailsView\":{{\"downloadFile\":[{{\"name\":\"x\"</script><a href='{GoodUrl}'>Download</a>");

        AzurePublishedFile? file = AzureDownloadPage.TryFind(page);

        Assert.NotNull(file);
        Assert.Equal("ServiceTags_Public_20261012.json", file.FileName);
    }

    [Fact]
    public void Embedded_Details_Win_Over_A_Different_Button_Link()
    {
        const string other = "https://download.microsoft.com/download/a/b/c/ServiceTags_Public_20260101.json";
        byte[] page = Bytes(
            "<script>window.__DLCDetails__={\"dlcDetailsView\":{\"downloadFile\":[{\"name\":\"n\",\"url\":\"" + GoodUrl + "\"}]}};</script>" +
            $"<a href=\"{other}\">Old</a>");

        Assert.Equal(new Uri(GoodUrl), AzureDownloadPage.TryFind(page)!.Url);
    }

    [Fact]
    public void Takes_The_Newest_Of_Several_Valid_Files()
    {
        const string older = "https://download.microsoft.com/download/a/b/c/ServiceTags_Public_20261005.json";
        byte[] page = Bytes(
            "<script>window.__DLCDetails__={\"dlcDetailsView\":{\"downloadFile\":[" +
            "{\"name\":\"n\",\"url\":\"" + older + "\"},{\"name\":\"n\",\"url\":\"" + GoodUrl + "\"}]}};</script>");

        Assert.Equal(new DateOnly(2026, 10, 12), AzureDownloadPage.TryFind(page)!.FileDate);
    }

    [Fact]
    public void Skips_An_Invalid_Entry_And_Uses_The_Next_Valid_One()
    {
        byte[] page = Bytes(
            "<script>window.__DLCDetails__={\"dlcDetailsView\":{\"downloadFile\":[" +
            "{\"name\":\"n\",\"url\":\"http://download.microsoft.com/x/ServiceTags_Public_20261012.json\"}," +
            "{\"name\":\"n\",\"url\":42},{\"name\":\"n\"},7,{\"name\":\"n\",\"url\":\"" + GoodUrl + "\"}]}};</script>");

        Assert.Equal(new Uri(GoodUrl), AzureDownloadPage.TryFind(page)!.Url);
    }

    [Theory]
    [InlineData("http://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("https://download.microsoft.com.evil.example/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("https://evil-download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("https://www.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("https://download.microsoft.com:8443/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("https://user@download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json?x=1")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json#frag")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_AzureGovernment_20261012.json")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_China_20261012.json")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261332.json")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_Public_2026101.json")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json.exe")]
    [InlineData("https://download.microsoft.com/download/7/1/d/servicetags_public_20261012.json")]
    [InlineData("https://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json/")]
    [InlineData("https://download.microsoft.com/download/7/1/d/%2e%2e/ServiceTags_Public_20261012.json%00")]
    [InlineData("ftp://download.microsoft.com/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("/download/7/1/d/ServiceTags_Public_20261012.json")]
    [InlineData("")]
    public void Rejects_A_Link_That_Is_Not_The_Public_Service_Tags_File(string url)
    {
        Assert.Null(AzureDownloadPage.TryFind(Bytes(EmbeddedPage(url))));
        Assert.Null(AzureDownloadPage.TryFind(Bytes($"<a href=\"{url}\">Download</a>")));
    }

    [Fact]
    public void Returns_Null_For_A_Page_With_No_Link()
    {
        Assert.Null(AzureDownloadPage.TryFind(Bytes("<html><body>Sorry, nothing here.</body></html>")));
        Assert.Null(AzureDownloadPage.TryFind([]));
        Assert.Null(AzureDownloadPage.TryFind(Bytes("window.__DLCDetails__=")));
        Assert.Null(AzureDownloadPage.TryFind(Bytes("window.__DLCDetails__=[1,2,3]")));
    }

    [Fact]
    public void Ignores_Links_To_Other_Pages_Before_The_Right_One()
    {
        byte[] page = Bytes($"<a href=\"https://www.microsoft.com/en-us/\">Home</a><a href=\"{GoodUrl}\">Download</a>");

        Assert.Equal(new Uri(GoodUrl), AzureDownloadPage.TryFind(page)!.Url);
    }

    private static string EmbeddedPage(string url) =>
        "<script>window.__DLCDetails__={\"dlcDetailsView\":{\"downloadFile\":[{\"name\":\"n\",\"url\":\"" + url + "\"}]}};</script>";

    private static string RealPageShape() =>
        "<!DOCTYPE html><html lang=\"en-us\"><head><title>Download Azure IP Ranges and Service Tags \u2013 Public Cloud from Official Microsoft Download Center</title></head><body>" +
        "<script>window.__DLCDetails__={\"dlcDetailsView\":{\"error\":\"\",\"downloadTitle\":\"Azure IP Ranges and Service Tags \u2013 Public Cloud\"," +
        "\"downloadFile\":[{\"isPrimary\":\"False\",\"name\":\"ServiceTags_Public_20261012.json\",\"url\":\"" + GoodUrl + "\"," +
        "\"size\":\"4388432\",\"version\":\"2026.10.12\",\"datePublished\":\"10/15/2026 10:29:17 AM\"}],\"locale\":\"en-us\"}}</script>" +
        "<div id=\"rootContainer_DLCDetails\"><a href=\"" + GoodUrl + "\" download=\"\" class=\"dlcdetail__download-btn btn btn-primary\">Download</a></div></body></html>";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
