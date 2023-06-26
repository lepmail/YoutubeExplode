using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Videos.ClosedCaptions;

/// <summary>
/// Operations related to closed captions of YouTube videos.
/// </summary>
public class ClosedCaptionClient
{
    private readonly ClosedCaptionController _controller;

    /// <summary>
    /// Initializes an instance of <see cref="ClosedCaptionClient" />.
    /// </summary>
    public ClosedCaptionClient(HttpClient http) => _controller = new ClosedCaptionController(http);

    private async IAsyncEnumerable<ClosedCaptionTrackInfo> GetClosedCaptionTrackInfosAsync(
        VideoId videoId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use the TVHTML5 client instead of ANDROID_TESTSUITE because the latter doesn't provide closed captions
        var playerResponse = await _controller.GetPlayerResponseAsync(videoId, null, cancellationToken);

        foreach (var trackData in playerResponse.ClosedCaptionTracks)
        {
            var url =
                trackData.Url ??
                throw new YoutubeExplodeException("Could not extract track URL.");

            var languageCode =
                trackData.LanguageCode ??
                throw new YoutubeExplodeException("Could not extract track language code.");

            var languageName =
                trackData.LanguageName ??
                throw new YoutubeExplodeException("Could not extract track language name.");

            yield return new ClosedCaptionTrackInfo(
                url,
                new Language(languageCode, languageName),
                trackData.IsAutoGenerated
            );
        }
    }

    /// <summary>
    /// Gets the manifest that lists available closed caption tracks for the specified video.
    /// </summary>
    public async ValueTask<ClosedCaptionManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default) =>
        new(await GetClosedCaptionTrackInfosAsync(videoId, cancellationToken));

    private async IAsyncEnumerable<ClosedCaption> GetClosedCaptionsAsync(
        ClosedCaptionTrackInfo trackInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetClosedCaptionTrackResponseAsync(trackInfo.Url, cancellationToken);

        foreach (var captionData in response.Captions)
        {
            var text = captionData.Text;

            // Skip over empty captions, but not captions containing only whitespace
            // https://github.com/Tyrrrz/YoutubeExplode/issues/671
            if (string.IsNullOrEmpty(text))
                continue;

            // Auto-generated captions may be missing offset or duration.
            // https://github.com/Tyrrrz/YoutubeExplode/discussions/619
            if (captionData.Offset is not { } offset ||
                captionData.Duration is not { } duration)
            {
                continue;
            }

            var parts = new List<ClosedCaptionPart>();
            foreach (var partData in captionData.Parts)
            {
                var partText = partData.Text;

                // Skip over empty parts, but not parts containing only whitespace
                // https://github.com/Tyrrrz/YoutubeExplode/issues/671
                if (string.IsNullOrEmpty(partText))
                    continue;

                var partOffset =
                    partData.Offset ??
                    throw new YoutubeExplodeException("Could not extract caption part offset.");

                var part = new ClosedCaptionPart(partText, partOffset);

                parts.Add(part);
            }

            yield return new ClosedCaption(
                text,
                offset,
                duration,
                parts
            );
        }
    }

    /// <summary>
    /// Gets the closed caption track identified by the specified metadata.
    /// </summary>
    public async ValueTask<ClosedCaptionTrack> GetAsync(
        ClosedCaptionTrackInfo trackInfo,
        CancellationToken cancellationToken = default) =>
        new(await GetClosedCaptionsAsync(trackInfo, cancellationToken));

    /// <summary>
    /// Writes the closed caption track identified by the specified metadata to the specified writer.
    /// </summary>
    /// <remarks>
    /// Closed captions are written in the SRT file format.
    /// </remarks>
    public async ValueTask WriteToAsync(
        ClosedCaptionTrackInfo trackInfo,
        TextWriter writer,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Would be better to use GetClosedCaptionsAsync(...) instead for streaming,
        // but we need the total number of captions to report progress.
        var track = await GetAsync(trackInfo, cancellationToken);

        var buffer = new StringBuilder();
        foreach (var (caption, i) in track.Captions.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();

            buffer
                // Line number
                .AppendLine((i + 1).ToString())
                // Time start --> time end
                .Append(caption.Offset.ToLongString(CultureInfo.InvariantCulture))
                .Append(" --> ")
                .Append((caption.Offset + caption.Duration).ToLongString(CultureInfo.InvariantCulture))
                .AppendLine()
                // Content
                .AppendLine(caption.Text);

            await writer.WriteLineAsync(buffer.ToString());
            buffer.Clear();

            progress?.Report((i + 1.0) / track.Captions.Count);
        }
    }

    /// <summary>
    /// Downloads the closed caption track identified by the specified metadata to the specified file.
    /// </summary>
    /// <remarks>
    /// Closed captions are written in the SRT file format.
    /// </remarks>
    public async ValueTask DownloadAsync(
        ClosedCaptionTrackInfo trackInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var writer = File.CreateText(filePath);
        await WriteToAsync(trackInfo, writer, progress, cancellationToken);
    }
}