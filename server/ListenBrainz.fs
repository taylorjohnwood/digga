module Digga.ListenBrainz

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open Microsoft.Extensions.Logging
open Digga.Domain

type Client =
    { Http: HttpClient
      Token: string option
      Logger: ILogger }

let private jsonOptions =
    JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

/// The submit-listens body for one listen (https://listenbrainz.readthedocs.io/en/latest/users/json.html).
let payload (subject: Db.ListenSubject) (listenedAt: DateTimeOffset) : string =
    let body =
        {| listen_type = "single"
           payload =
            [| {| listened_at = listenedAt.ToUnixTimeSeconds()
                  track_metadata =
                   {| artist_name = Names.cleanArtist subject.Artist
                      track_name = Names.trackNameFromVideoTitle subject.Artist subject.VideoTitle
                      release_name = subject.ReleaseTitle
                      additional_info =
                       {| media_player = "YouTube"
                          music_service = "youtube.com"
                          origin_url = $"https://www.youtube.com/watch?v={subject.YoutubeId}"
                          submission_client = "digga"
                          submission_client_version = "0.1"
                          duration_ms =
                           (if subject.DurationSeconds > 0 then Nullable(subject.DurationSeconds * 1000) else Nullable()) |} |} |} |] |}

    JsonSerializer.Serialize(body, jsonOptions)

let submit (client: Client) (subject: Db.ListenSubject) (listenedAt: DateTimeOffset) (ct: CancellationToken) =
    task {
        match client.Token with
        | None ->
            client.Logger.LogDebug("Not submitting listen of {YoutubeId} to ListenBrainz: LISTENBRAINZ_TOKEN not set", subject.YoutubeId)
            return NotConfigured
        | Some token ->
            let body = payload subject listenedAt
            use request = new HttpRequestMessage(HttpMethod.Post, "https://api.listenbrainz.org/1/submit-listens")
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {token}") |> ignore
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            try
                use! response = client.Http.SendAsync(request, ct)
                let! responseBody = response.Content.ReadAsStringAsync ct

                match response.IsSuccessStatusCode with
                | true ->
                    client.Logger.LogInformation(
                        "Submitted listen of {YoutubeId} (release {ReleaseId}) to ListenBrainz: {Payload}",
                        subject.YoutubeId, subject.ReleaseId, body)
                    return Submitted
                | false ->
                    client.Logger.LogWarning(
                        "ListenBrainz rejected listen of {YoutubeId}: {Status} {Response}; payload {Payload}",
                        subject.YoutubeId, int response.StatusCode, responseBody, body)
                    return SubmitFailed $"HTTP {int response.StatusCode}: {responseBody}"
            with ex when not (ex :? OperationCanceledException) ->
                client.Logger.LogWarning(ex, "ListenBrainz submission of {YoutubeId} failed before a response", subject.YoutubeId)
                return SubmitFailed ex.Message
    }
