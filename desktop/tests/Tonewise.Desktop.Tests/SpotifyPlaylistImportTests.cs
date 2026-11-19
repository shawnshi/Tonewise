using System.Net;
using System.Text;
using Tonewise.SyncHost.Playlists;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class SpotifyPlaylistImportTests
{
    private const string EmbedHtml2010s = """
        <!DOCTYPE html>
        <html lang="en">
          <head><title>All Out 2010s | Spotify</title></head>
          <body>
            <script id="__NEXT_DATA__" type="application/json">
            {
              "props": {
                "pageProps": {
                  "state": {
                    "data": {
                      "entity": {
                        "type": "playlist",
                        "id": "37i9dQZF1DX5Ejj0EkURtP",
                        "name": "All Out 2010s",
                        "trackList": [
                          {
                            "entityType": "track",
                            "title": "Beauty And A Beat",
                            "subtitle": "Justin Bieber,\u00A0Nicki Minaj",
                            "duration": 227986
                          },
                          {
                            "entityType": "track",
                            "title": "Feel So Close - Radio Edit",
                            "subtitle": "Calvin Harris",
                            "duration": 206413
                          },
                          {
                            "entityType": "episode",
                            "title": "Podcast Episode Should Be Skipped",
                            "subtitle": "Host",
                            "duration": 60000
                          },
                          {
                            "entityType": "track",
                            "title": "I Took A Pill In Ibiza - Seeb Remix",
                            "subtitle": "Mike Posner,\u00A0Seeb",
                            "duration": 197933
                          }
                        ]
                      }
                    }
                  }
                }
              }
            }
            </script>
          </body>
        </html>
        """;

    private const string EmbedHtml2000s = """
        <!DOCTYPE html>
        <html lang="en">
          <head><title>All Out 2000s - Spotify</title></head>
          <body>
            <script id="__NEXT_DATA__" type="application/json">
            {
              "props": {
                "pageProps": {
                  "state": {
                    "data": {
                      "entity": {
                        "type": "playlist",
                        "id": "37i9dQZF1DX4o1oenSJRJd",
                        "name": "All Out 2000s",
                        "trackList": [
                          {
                            "entityType": "track",
                            "title": "Oops!...I Did It Again",
                            "subtitle": "Britney Spears",
                            "duration": 211160
                          },
                          {
                            "entityType": "track",
                            "title": "The Sweet Escape",
                            "subtitle": "Gwen Stefani,\u00A0Akon",
                            "duration": 246466
                          }
                        ]
                      }
                    }
                  }
                }
              }
            }
            </script>
          </body>
        </html>
        """;

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DX5Ejj0EkURtP", "37i9dQZF1DX5Ejj0EkURtP")]
    [InlineData("https://open.spotify.com/intl-zh/playlist/37i9dQZF1DX4o1oenSJRJd?si=abc123", "37i9dQZF1DX4o1oenSJRJd")]
    [InlineData("https://open.spotify.com/user/spotify/playlist/37i9dQZF1DX5Ejj0EkURtP", "37i9dQZF1DX5Ejj0EkURtP")]
    [InlineData("https://open.spotify.com/embed/playlist/37i9dQZF1DX5Ejj0EkURtP", "37i9dQZF1DX5Ejj0EkURtP")]
    [InlineData("spotify:playlist:37i9dQZF1DX4o1oenSJRJd", "37i9dQZF1DX4o1oenSJRJd")]
    public void TryExtractSpotifyPlaylistId_SupportsAllPlaylistFormats(string input, string expectedId)
    {
        Assert.True(PlaylistImporter.TryExtractSpotifyPlaylistId(input, out string id));
        Assert.Equal(expectedId, id);
    }

    [Fact]
    public async Task FetchAndParseUrlAsync_RewritesSpotifyUrlsAndUris_CleansNbsp_AndResolvesShortLinkRedirects()
    {
        var handler = new StubSpotifyHttpHandler();
        using var http = new HttpClient(handler);

        var p1 = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://open.spotify.com/playlist/37i9dQZF1DX5Ejj0EkURtP",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Spotify, p1.SourceKind);
        Assert.Equal("All Out 2010s", p1.Title);
        Assert.Equal(3, p1.Tracks.Count);
        Assert.Equal("Beauty And A Beat", p1.Tracks[0].Title);
        Assert.Equal("Justin Bieber, Nicki Minaj", p1.Tracks[0].Artist);
        Assert.DoesNotContain('\u00A0', p1.Tracks[0].Artist);
        Assert.Equal("Mike Posner, Seeb", p1.Tracks[2].Artist);
        Assert.DoesNotContain('\u00A0', p1.Tracks[2].Artist);
        Assert.Contains("https://open.spotify.com/embed/playlist/37i9dQZF1DX5Ejj0EkURtP", handler.RequestedUrls);

        var p2 = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://open.spotify.com/intl-zh/playlist/37i9dQZF1DX4o1oenSJRJd?si=test",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Spotify, p2.SourceKind);
        Assert.Equal("All Out 2000s", p2.Title);
        Assert.Equal(2, p2.Tracks.Count);
        Assert.Equal("Gwen Stefani, Akon", p2.Tracks[1].Artist);
        Assert.DoesNotContain('\u00A0', p2.Tracks[1].Artist);

        var pEmbed = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://open.spotify.com/embed/playlist/37i9dQZF1DX5Ejj0EkURtP",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Spotify, pEmbed.SourceKind);
        Assert.Equal(3, pEmbed.Tracks.Count);

        var pUri = await PlaylistImporter.FetchAndParseUrlAsync(
            "spotify:playlist:37i9dQZF1DX4o1oenSJRJd",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Spotify, pUri.SourceKind);
        Assert.Equal("All Out 2000s", pUri.Title);

        var pShort = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://spotify.link/demoShortLink",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Spotify, pShort.SourceKind);
        Assert.Equal("All Out 2010s", pShort.Title);
        Assert.Equal(3, pShort.Tracks.Count);
    }

    [Fact]
    public async Task FetchAndParseUrlAsync_RejectsGenericHtml_AndSpaShellWithoutNextData()
    {
        var handler = new StubSpotifyHttpHandler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PlaylistImporter.FetchAndParseUrlAsync("https://example.com/music-page", httpClient: http));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PlaylistImporter.FetchAndParseUrlAsync(
                "https://open.spotify.com/playlist/0000000000EmptySpaShell",
                httpClient: http));

        var parsedBandSlash = PlaylistImporter.ParseTextPlaylist("AC/DC - Back In Black\nArtists/Shallow.flac");
        Assert.Equal(2, parsedBandSlash.Tracks.Count);
        Assert.Equal("AC/DC", parsedBandSlash.Tracks[0].Artist);
        Assert.Equal("Back In Black", parsedBandSlash.Tracks[0].Title);
        Assert.Null(parsedBandSlash.Tracks[0].FilePath);
        Assert.Equal("Artists/Shallow.flac", parsedBandSlash.Tracks[1].FilePath);
    }

    [Fact]
    public void PlaylistTrackMatcher_NormalizesSpotifyEditionSuffixes_AndMatchesLocalTracks()
    {
        Assert.Equal("feel so close", PlaylistTrackMatcher.NormalizeTitle("Feel So Close - Radio Edit"));
        Assert.Equal("all the stars", PlaylistTrackMatcher.NormalizeTitle("All The Stars (with SZA) - From \"Black Panther: The Album\""));
        Assert.Equal("lady marmalade", PlaylistTrackMatcher.NormalizeTitle("Lady Marmalade - From \"Moulin Rouge\" Soundtrack"));
        Assert.Equal("bohemian rhapsody", PlaylistTrackMatcher.NormalizeTitle("Bohemian Rhapsody - Remastered 2011"));
        Assert.Equal("i took a pill in ibiza seeb remix", PlaylistTrackMatcher.NormalizeTitle("I Took A Pill In Ibiza - Seeb Remix"));

        var library = new List<LocalLibraryTrackRef>
        {
            new("uid1", "Calvin Harris/18 Months/Feel So Close.flac", @"D:\Music\Calvin Harris\18 Months\Feel So Close.flac", "Feel So Close", "Calvin Harris", "18 Months", 206000, 44100),
            new("uid2", "Kendrick Lamar/Black Panther/All The Stars.flac", @"D:\Music\Kendrick Lamar\Black Panther\All The Stars.flac", "All The Stars", "Kendrick Lamar, SZA", "Black Panther", 232000, 44100),
            new("uid3", "Christina Aguilera/Moulin Rouge/Lady Marmalade.flac", @"D:\Music\Christina Aguilera\Moulin Rouge\Lady Marmalade.flac", "Lady Marmalade", "Christina Aguilera", "Moulin Rouge", 264000, 44100),
            new("uid4", "Queen/A Night at the Opera/Bohemian Rhapsody.flac", @"D:\Music\Queen\A Night at the Opera\Bohemian Rhapsody.flac", "Bohemian Rhapsody", "Queen", "A Night at the Opera", 354000, 44100),
            new("uid5", "Mike Posner/At Night Alone/I Took A Pill In Ibiza.flac", @"D:\Music\Mike Posner\At Night Alone\I Took A Pill In Ibiza.flac", "I Took A Pill In Ibiza", "Mike Posner", "At Night Alone", 280000, 44100),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("Feel So Close - Radio Edit", "Calvin Harris"),
            new("All The Stars (with SZA) - From \"Black Panther: The Album\"", "Kendrick Lamar, SZA"),
            new("Lady Marmalade - From \"Moulin Rouge\" Soundtrack", "Christina Aguilera, Lil' Kim"),
            new("Bohemian Rhapsody - Remastered 2011", "Queen"),
            new("I Took A Pill In Ibiza - Seeb Remix", "Mike Posner, Seeb"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.True(resolved[0].IsLocalMatched);
        Assert.Equal("uid1", resolved[0].MatchedTrackUid);
        Assert.True(resolved[1].IsLocalMatched);
        Assert.Equal("uid2", resolved[1].MatchedTrackUid);
        Assert.True(resolved[2].IsLocalMatched);
        Assert.Equal("uid3", resolved[2].MatchedTrackUid);
        Assert.True(resolved[3].IsLocalMatched);
        Assert.Equal("uid4", resolved[3].MatchedTrackUid);
        Assert.False(resolved[4].IsLocalMatched);
    }

    private sealed class StubSpotifyHttpHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            RequestedUrls.Add(url);

            if (url == "https://open.spotify.com/embed/playlist/37i9dQZF1DX5Ejj0EkURtP")
            {
                return Task.FromResult(HtmlResponse(EmbedHtml2010s, request));
            }
            if (url == "https://open.spotify.com/embed/playlist/37i9dQZF1DX4o1oenSJRJd")
            {
                return Task.FromResult(HtmlResponse(EmbedHtml2000s, request));
            }
            if (url == "https://open.spotify.com/embed/playlist/0000000000EmptySpaShell")
            {
                return Task.FromResult(HtmlResponse("<!DOCTYPE html><html><head></head><body><script id=\"appServerConfig\">{}</script></body></html>", request));
            }
            if (url == "https://spotify.link/demoShortLink")
            {
                var redirectedReq = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/playlist/37i9dQZF1DX5Ejj0EkURtP?si=redir");
                return Task.FromResult(HtmlResponse("<html><body>redirected</body></html>", redirectedReq));
            }
            if (url.StartsWith("https://example.com/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(HtmlResponse("<!DOCTYPE html><html><head><title>Some Site</title></head><body><div>not a playlist</div></body></html>", request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage HtmlResponse(string html, HttpRequestMessage req) => new(HttpStatusCode.OK)
        {
            RequestMessage = req,
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
    }
}
