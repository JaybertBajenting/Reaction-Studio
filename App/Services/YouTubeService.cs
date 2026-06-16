using System.IO;
using System.Reflection;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace ReactionStudio.Services;

public static class YTUploader
{
    private static string SecretFile => AppPaths.Secret;
    private static string TokenDir   => AppPaths.AuthData;

    public static async Task UploadAsync(string videoPath, string thumbPath,
                                          string title, IProgress<string> progress,
                                          DateTimeOffset? publishAt = null)
    {
        if (!File.Exists(SecretFile))
            throw new FileNotFoundException(
                $"Place client_secret.json here:\n{Path.GetDirectoryName(SecretFile)}");

        progress.Report("Authenticating with Google…");
        var secrets = GoogleClientSecrets.FromFile(SecretFile).Secrets;
        var creds   = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            ["https://www.googleapis.com/auth/youtube.upload"],
            "user",
            CancellationToken.None,
            new Google.Apis.Util.Store.FileDataStore(TokenDir, true)
        );

        var svc = new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = creds,
            ApplicationName       = "ReactionStudio"
        });

        var video = new Video
        {
            Snippet = new VideoSnippet
            {
                Title       = title,
                Tags        = ["shorts", "reaction", "funny"],
                CategoryId  = "23"
            },
            Status = new VideoStatus
            {
                // Scheduled publish: stays private, then auto-goes public at publishAt.
                PrivacyStatus           = "private",
                SelfDeclaredMadeForKids = false,
                // "AI use / altered content" disclosure → always answer "No"
                // (genuine reactions: not realistic synthetic/altered media).
                ContainsSyntheticMedia  = false,
                PublishAtDateTimeOffset = publishAt
            }
        };

        progress.Report("Uploading video…");
        using var stream = File.OpenRead(videoPath);
        var insert = svc.Videos.Insert(video, "snippet,status", stream, "video/mp4");
        insert.ProgressChanged += u =>
        {
            if (u.Status == UploadStatus.Uploading)
                progress.Report($"Uploading {u.BytesSent / 1_000_000.0:F1} MB…");
        };

        var result = await insert.UploadAsync();
        if (result.Status == UploadStatus.Failed)
            throw new Exception(result.Exception?.Message ?? "Upload failed");

        string videoId = insert.ResponseBody.Id;
        progress.Report("Setting thumbnail…");

        if (File.Exists(thumbPath))
        {
            using var tStream = File.OpenRead(thumbPath);
            await svc.Thumbnails.Set(videoId, tStream, "image/jpeg").UploadAsync();
        }

        if (publishAt.HasValue)
            progress.Report($"✅  Scheduled → goes PUBLIC {ScheduleService.Describe(publishAt.Value)}  ·  youtube.com/watch?v={videoId}");
        else
            progress.Report($"✅  Uploaded → youtube.com/watch?v={videoId}  (private)");
    }
}
