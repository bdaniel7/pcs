using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using PlexCompatibleServer.Api;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class RequestLogTests
{
    private static DefaultHttpContext Context(string query = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/video";
        ctx.Request.QueryString = new QueryString(query);
        return ctx;
    }

    [Test]
    public void DescribeIdentity_redacts_token_header_but_keeps_other_identity()
    {
        var ctx = Context();
        ctx.Request.Headers["X-Plex-Token"] = "header-secret";
        ctx.Request.Headers["X-Plex-Client-Identifier"] = "abc";

        var described = RequestLog.DescribeIdentity(ctx);

        Assert.That(described, Does.Not.Contain("header-secret"));
        Assert.That(described, Does.Contain("X-Plex-Token=***"));
        Assert.That(described, Does.Contain("X-Plex-Client-Identifier=abc"));
    }

    [Test]
    public void DescribeIdentity_redacts_token_query_parameter()
    {
        var ctx = Context("?X-Plex-Token=query-secret&X-Plex-Client-Identifier=abc");

        var described = RequestLog.DescribeIdentity(ctx);

        Assert.That(described, Does.Not.Contain("query-secret"));
        Assert.That(described, Does.Contain("X-Plex-Token=***"));
        Assert.That(described, Does.Contain("X-Plex-Client-Identifier=abc"));
    }

    [Test]
    public void DescribeTarget_redacts_token_and_preserves_surrounding_parameters()
    {
        var ctx = Context("?X-Plex-Client-Identifier=abc&X-Plex-Token=query-secret&path=/movie");

        var target = RequestLog.DescribeTarget(ctx);

        Assert.That(target, Does.Not.Contain("query-secret"));
        Assert.That(target, Does.Contain("X-Plex-Token=***"));
        Assert.That(target, Does.Contain("X-Plex-Client-Identifier=abc"));
        Assert.That(target, Does.Contain("path=/movie"));
    }

    [Test]
    public void DescribeTarget_redacts_token_when_it_is_the_last_parameter()
    {
        var ctx = Context("?X-Plex-Client-Identifier=abc&X-Plex-Token=query-secret");

        var target = RequestLog.DescribeTarget(ctx);

        Assert.That(target, Does.Not.Contain("query-secret"));
        Assert.That(target, Does.Contain("X-Plex-Token=***"));
    }

    [Test]
    public void DescribeTarget_leaves_request_untouched_without_a_token()
    {
        var ctx = Context("?X-Plex-Client-Identifier=abc");

        var target = RequestLog.DescribeTarget(ctx);

        Assert.That(target, Is.EqualTo("/video?X-Plex-Client-Identifier=abc"));
    }
}
