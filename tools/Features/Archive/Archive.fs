﻿namespace Backbeat.Features

open System
open System.Linq
open System.IO
open System.Collections.Generic
open Percyqaz.Json
open Percyqaz.Common
open Prelude
open Prelude.Charts.Formats.Conversions
open Prelude.Data.Charts.Archive
open Backbeat.Utils

module Archive =

    let artists = 
        match JSON.FromFile (Path.Combine(ARCHIVE_PATH, "artists.json")) with
        | Ok d -> d
        | Error e -> 
            Logging.Warn(sprintf "Error loading artists.json: %s" e.Message)
            Dictionary<ArtistName, Artist>()
    
    let songs : Songs = 
        match JSON.FromFile (Path.Combine(ARCHIVE_PATH, "songs.json")) with
        | Ok d -> d
        | Error e -> 
            Logging.Warn(sprintf "Error loading songs.json: %s" e.Message)
            Dictionary<SongId, Song>()
    
    let charts : Charts = 
        match JSON.FromFile (Path.Combine(ARCHIVE_PATH, "charts.json")) with
        | Ok d -> d
        | Error e -> 
            Logging.Warn(sprintf "Error loading charts.json: %s" e.Message)
            Dictionary<ChartHash, Chart>()
    
    let packs : Packs = 
        match JSON.FromFile (Path.Combine(ARCHIVE_PATH, "packs.json")) with
        | Ok d -> d
        | Error e -> 
            Logging.Warn(sprintf "Error loading packs.json: %s" e.Message)
            {
                Stepmania = Dictionary<StepmaniaPackId, StepmaniaPack>()
                Community = Dictionary<CommunityPackId, CommunityPack>()
            }

    let save() =
        try
            JSON.ToFile (Path.Combine(ARCHIVE_PATH, "artists.json"), true) artists
            JSON.ToFile (Path.Combine(ARCHIVE_PATH, "songs.json"), true) songs
            JSON.ToFile (Path.Combine(ARCHIVE_PATH, "charts.json"), true) charts
            JSON.ToFile (Path.Combine(ARCHIVE_PATH, "packs.json"), true) packs 
            Logging.Debug("Saved database")
        with e ->
            Logging.Error("Database not saved", e)
            
    
    let wipe_archive() =
        charts.Clear()
        save()

    [<Json.AutoCodec>]
    type EOPackAttrs =
        {
            name: string
            average: float
            download: string
            mirror: string
            size: int64
        }
    
    [<Json.AutoCodec>]
    type EOPack =
        {
            ``type``: string
            id: int
            attributes: EOPackAttrs
        }

    let into_base64 (str: string) =
        str
        |> Text.Encoding.UTF8.GetBytes
        |> Convert.ToBase64String

    let from_base64 (str: string) =
        str
        |> Convert.FromBase64String
        |> Text.Encoding.UTF8.GetString

    let archive_download_link(str: string) =
        str.Replace("https://", "").Replace("http://", "") |> into_base64

    let load_stepmania_packs() =
        async {
            match! Data.WebServices.download_json_async("https://api.etternaonline.com/v2/packs/") with
            | None -> printfn "Failed to get EO packs"
            | Some (d: {| data: ResizeArray<EOPack> |}) ->
                for p in d.data do
                    if packs.Stepmania.ContainsKey(p.id) then packs.Stepmania.Remove(p.id) |> ignore
                    packs.Stepmania.Add(p.id,
                            {
                                Title = p.attributes.name
                                Mirrors = [archive_download_link p.attributes.download; archive_download_link p.attributes.mirror] |> List.distinct
                                Size = p.attributes.size
                            })
        }
        |> Async.RunSynchronously
        save()

    module Queue =
        let get id =
            try
                File.ReadAllLines (Path.Combine(ARCHIVE_PATH, "queue", id+".txt"))
                |> List.ofArray
            with :? FileNotFoundException -> []

        let save id xs =
            File.WriteAllLines (Path.Combine(ARCHIVE_PATH, "queue", id+".txt"), Array.ofList xs)

        let append id x =
            File.AppendAllText (Path.Combine(ARCHIVE_PATH, "queue", id+".txt"), x + "\n")

    let simplify_string =
        let regex = Text.RegularExpressions.Regex("[^\sa-zA-Z0-9_-]")
        fun (s: string) -> regex.Replace(s.ToLowerInvariant(), "").Trim().Replace(" ", "")

    let add_artist(artist: string) =
        if artists.ContainsKey(artist) then ()
        else artists.Add(artist, { Alternatives = [artist] })

    let add_artist_alias(artist, alias) =
        if artists.ContainsKey(artist) then 
            artists.[artist].Add alias
        elif artists.ContainsKey(alias) then
            artists.[alias].Add artist
        else Logging.Info(sprintf "Can't add alias %s to %s because neither exists in artist list" alias artist)

    let add_song(song: Song) : SongId =
        List.iter add_artist song.Artists
        List.iter add_artist song.OtherArtists
        List.iter add_artist song.Remixers

        let stitle = simplify_string song.Title
        let mutable existing_id = None
        let mutable similar_id = None
        for other_song_id in songs.Keys do
            if existing_id.IsNone then
                let other_song = songs.[other_song_id]
                if other_song = song then existing_id <- Some other_song_id
                let mutable similarity = 0

                if stitle = simplify_string other_song.Title then similarity <- similarity + 1
                if song.Source = other_song.Source then similarity <- similarity + 1
                if List.contains song.Title other_song.AlternativeTitles then similarity <- similarity + 1
                if List.contains song.Title other_song.AlternativeTitles then similarity <- similarity + 1
                if other_song.Tags.Length - (List.except song.Tags other_song.Tags).Length > 2 then similarity <- similarity + 1
                if other_song.Artists.Length - (List.except song.Artists other_song.Artists).Length > 1 then similarity <- similarity + 1

                if similarity >= 2 && existing_id.IsNone then
                    similar_id <- Some other_song_id
                    Logging.Info(sprintf "Song similarity (but not identical) to %s" other_song_id)

        match existing_id with
        | None ->
            let id = (simplify_string (List.head song.Artists)) + "/" + (simplify_string song.Title)
            let mutable i = 0
            while songs.ContainsKey(if i > 0 then id + "-" + i.ToString() else id) do
                i <- i + 1
            let id = if i > 0 then id + "-" + i.ToString() else id
            songs.Add(id, song)
            Logging.Info(sprintf "+ Song: %s" id)
            match similar_id with
            | Some s -> Queue.append "songs-similar" (s + ", " + id)
            | None -> ()
            id
        | Some id -> id

    let slurp_chart(chart: Charts.Formats.Interlude.Chart) =
        if not (File.Exists chart.AudioPath) then
            Logging.Info(sprintf "Rejecting %s because it doesn't have audio" chart.Header.Title)
        elif not (File.Exists chart.BackgroundPath) then
            Logging.Info(sprintf "Rejecting %s because it doesn't have a background image" chart.Header.Title)
        else

        let song: Song =
            {
                Artists = [chart.Header.Artist]
                OtherArtists = []
                Remixers = []
                Title = chart.Header.Title
                AlternativeTitles = match chart.Header.TitleNative with Some x -> [x] | None -> []
                Source = chart.Header.Source
                Tags = chart.Header.Tags
            }
        let id = add_song song
        match chart.Header.ArtistNative with Some a -> add_artist_alias (chart.Header.Artist, a) | None -> ()
        
        let chartEntry: Chart = 
            {
                SongId = id
                Creators = [chart.Header.Creator]
                Keys = chart.Keys
                DifficultyName = chart.Header.DiffName
                Subtitle = chart.Header.Subtitle
                Tags = chart.Header.Tags
                Duration = chart.LastNote - chart.FirstNote
                Notecount = -1 //todo
                BPM = (0.0f<ms/beat>, 0.0f<ms/beat>) //todo
                Sources = 
                    match chart.Header.ChartSource with
                    | Charts.Formats.Interlude.ChartSource.Osu (set, id) -> [Osu {| BeatmapSetId = set; BeatmapId = id |}]
                    | Charts.Formats.Interlude.ChartSource.Stepmania (id) -> [Stepmania id]
                    | Charts.Formats.Interlude.ChartSource.Unknown -> []
                LastUpdated = DateTime.Now
            }
        let hash = Charts.Formats.Interlude.Chart.hash chart
        charts.Add(hash, chartEntry)
        Logging.Info(sprintf "+ Chart: %s" hash)

    let slurp_song_folder (sm_pack_id: int option) (target: string) =
        for file in Directory.EnumerateFiles target do
            match file with
            | ChartFile _ ->
                try
                    let a = 
                        { 
                            Config = { ConversionActionConfig.Default with StepmaniaPackId = sm_pack_id; CopyMediaFiles = false }
                            Source = file
                            TargetDirectory = Path.Combine (ARCHIVE_PATH, "yav")
                        }
                    loadAndConvertFile a
                    |> List.map (relocateChart a)
                    |> List.iter slurp_chart
                with err -> Logging.Info(sprintf "Failed to load/convert file: %s" target)
            | _ -> ()

    let slurp_pack_folder (sm_pack_id: int option) (target: string) =
        for songFolder in Directory.EnumerateDirectories target do slurp_song_folder sm_pack_id songFolder

    let rec try_download_mirrors(mirrors, filepath) : Async<bool> =
        async {
            match mirrors with
            | [] -> return false
            | url :: xs ->
                if not (File.Exists filepath) then
                    Logging.Info(sprintf "Downloading from %s ..." url)
                    let! result = Prelude.Data.WebServices.download_file.RequestAsync(url, filepath, ignore)
                    if result then return true 
                    else 
                        Logging.Error(sprintf "Download from %s failed." url)
                        return! try_download_mirrors (xs, filepath)
                else 
                    Logging.Info(sprintf "Download from %s already on disk ..." url)
                    return true
        }

    open System.IO.Compression

    let download_pack(id: StepmaniaPackId) =
        async {
            let mirrors = packs.Stepmania.[id].Mirrors |> List.map from_base64 |> List.map (fun s -> "http://" + s)
            let zip = Path.Combine(ARCHIVE_PATH, "tmp", string id + ".zip")
            let folder = Path.Combine(ARCHIVE_PATH, "tmp", string id)
            match! try_download_mirrors(mirrors, zip) with
            | true ->
                if Directory.Exists(folder) then Directory.Delete(folder, true)
                ZipFile.ExtractToDirectory(zip, folder)
                let pack_folder = (Directory.GetDirectories folder)[0]
                slurp_pack_folder (Some id) pack_folder
                return true
            | false ->
                Logging.Error(sprintf "All mirrors failed.")
                return false
        }

    let slurp() =
        let mutable packs = Queue.get "pack-imports"
        while packs <> [] do
            if download_pack(int (List.head packs)) |> Async.RunSynchronously then 
                Queue.append "pack-imports-success" (List.head packs)
                save()
            else Queue.append "pack-imports-failed" (List.head packs)
            packs <- List.tail packs
            Queue.save "pack-imports" packs

    let script() =
        wipe_archive()
        let keywords = ["skwid"; "nuclear"; "compulsive"; "valedumps"; "minty"]
        Queue.save "pack-imports" []
        for p in packs.Stepmania.Keys do
            let title = packs.Stepmania.[p].Title.ToLower()
            if keywords |> List.forall (fun w -> title.Contains(w) |> not) then ()
            else
                Logging.Info packs.Stepmania.[p].Title
                Queue.append "pack-imports" (p.ToString())

    let remix_regex = Text.RegularExpressions.Regex("\\((.*?) Remix\\)$")
    let song_meta_checks (song_id: SongId) (song: Song) =
        let fmt = song.FormattedTitle
        let suggestion =
            if song.Artists.Length = 1 && song.OtherArtists = [] && song.Remixers = [] then

                let artist, title, remixers =
                    let artist_matches = remix_regex.Matches(song.Artists.Head)
                    let title_matches = remix_regex.Matches(song.Title)

                    if artist_matches.Count = 1 then
                        let r: string = artist_matches.[0].Groups.[1].Value
                        let original_artist = song.Artists.Head.Replace(artist_matches.First().Value, "").Trim()
                        original_artist, song.Title, r.Split([|"&"; ","; " and "|], StringSplitOptions.TrimEntries) |> List.ofArray

                    elif title_matches.Count = 1 then
                        let r: string = title_matches.[0].Groups.[1].Value
                        let original_title = song.Title.Replace(title_matches.First().Value, "").Trim()
                        song.Artists.Head, original_title, r.Split([|"&"; ","; " and "|], StringSplitOptions.TrimEntries) |> List.ofArray

                    else song.Artists.Head, song.Title, []

                let artists, features =
                    let ftSplit : string array = artist.Split([|"FEAT."; "FT."; "Feat."; "Ft."; "feat."; "ft."|], StringSplitOptions.TrimEntries)
                    ftSplit.[0].TrimEnd([|' '; '('|]).Split([|" VS "; " Vs "; " vs "; " vs. "; " Vs. "; " VS. "; "&"; ","; " and "|], StringSplitOptions.TrimEntries) |> List.ofArray,
                    if ftSplit.Length > 1 then
                        ftSplit.[1].TrimEnd([|' '; ')'|]).Split([|"&"; ","; " and "|], StringSplitOptions.TrimEntries) |> List.ofArray
                    else []

                if artists <> song.Artists || features <> song.OtherArtists || remixers <> song.Remixers || title <> song.Title then
                    Some { song with Artists = artists; OtherArtists = features; Remixers = remixers; Title = title }
                else None
            else None
        match suggestion with
        | Some suggestion ->
            Logging.Info(sprintf "Suggested metadata change\n%A\n vvv \n%A" song suggestion)
            Logging.Info(sprintf "New: %s" suggestion.FormattedTitle)
            Logging.Info(sprintf "Old: %s" fmt)
            Logging.Info("\noptions ::\n 1 - Accept changes\n 2 - Accept changes but mark for manual edit\n 3 - Ignore changes but mark for manual edit\n 4 - No correction needed, don't suggest for this song")
            let mutable option_chosen = false
            while not option_chosen do
                match Console.ReadKey().Key with
                | ConsoleKey.D1 -> songs.[song_id] <- suggestion; save(); option_chosen <- true
                | ConsoleKey.D2 -> songs.[song_id] <- suggestion; Queue.append "songs-review" song_id; save(); option_chosen <- true
                | ConsoleKey.D3 -> Queue.append "songs-review" song_id; option_chosen <- true
                | ConsoleKey.D4 -> Queue.append "songs-ignore" song_id; option_chosen <- true
                | _ -> ()
        | _ -> ()

    let check_all_songs() =
        let ignores = Queue.get "songs-ignore"
        let reviews = Queue.get "songs-review"
        for id in songs.Keys |> Seq.except ignores |> Seq.except reviews do
            song_meta_checks id songs.[id]

    let rename_artist (old_artist: string, new_artist: string) =
        let swap = (fun a -> if a = old_artist then new_artist else a)
        for id in songs.Keys do
            let song = songs.[id]
            songs.[id] <-
                { song with
                    Artists = List.map swap song.Artists
                    OtherArtists = List.map swap song.OtherArtists
                    Remixers = List.map swap song.Remixers
                }
        save()

    let rec levenshtein (a: char list) (b: char list) =
        if abs (a.Length - b.Length) > 5 then 100 else
        match a, b with
        | [], ys -> ys.Length
        | xs, [] -> xs.Length
        | x :: xs, y :: ys when x = y -> levenshtein xs ys
        | x :: xs, y :: ys ->
            let a = levenshtein (x :: xs) ys
            if a >= 100 then 100 else
            let b = levenshtein xs (y :: ys)
            if b >= 100 then 100 else
            let c = levenshtein xs ys
            if c >= 100 then 100 else
            let res = 1 + min (min a b) c
            if res > 5 then 100 else res

    let check_all_artists() =
        let artists = ResizeArray<string>()

        let distinct_artists = Queue.get "artists-distinct" |> Array.ofList

        let filter (name: string) =
            name.Length > 3 && String.forall Char.IsAscii name

        let check_artist (context: Song) (artist: string) =
            if artists.Contains artist then () else

            let b = List.ofSeq (artist.ToLower())
            let mutable closest_match = ""
            let mutable closest_match_v = artist.Length / 2
            for a in artists |> Array.ofSeq do
                if filter a && filter artist && not (distinct_artists.Contains (artist + "," + a)) then
                    let dist = levenshtein (List.ofSeq (a.ToLower())) b
                    if dist < closest_match_v then closest_match <- a; closest_match_v <- dist
                    
            let mutable artist = artist
            if closest_match <> "" then
                Logging.Info(sprintf "Possible artist match")
                Logging.Info(sprintf "Existing: %A" closest_match)
                Logging.Info(sprintf "Incoming: %A" artist)
                Logging.Info(sprintf " Context: %s" context.FormattedTitle)
                Logging.Info("\noptions ::\n 1 - Existing is correct\n 2 - Incoming is correct\n 3 - These are not the same artist")
                let mutable option_chosen = false
                while not option_chosen do
                    match Console.ReadKey().Key with
                    | ConsoleKey.D1 -> rename_artist (artist, closest_match); artists.Remove closest_match |> ignore; artist <- closest_match; option_chosen <- true
                    | ConsoleKey.D2 -> rename_artist (closest_match, artist); artists.Remove closest_match |> ignore; option_chosen <- true
                    | ConsoleKey.D3 -> Queue.append "artists-distinct" (artist + "," + closest_match); option_chosen <- true
                    | _ -> ()
            artists.Add artist
                    
        for id in songs.Keys |> Array.ofSeq do
            let song = songs.[id]
            List.iter (check_artist song) song.Artists
            List.iter (check_artist song) song.OtherArtists
            List.iter (check_artist song) song.Remixers
        
    open Percyqaz.Shell

    let register (ctx: Context) =
        ctx
            .WithCommand("script",
            Command.create "User script" [] <| Impl.Create(script))
            .WithCommand("get_eo", 
            Command.create "Archives EO packs locally" [] <| Impl.Create(load_stepmania_packs))
            .WithCommand("slurp", 
            Command.create "Processes queues of chart data to add to database" [] <| Impl.Create(slurp))
            .WithCommand("check_songs", 
            Command.create "Scan song metadata for mistakes" [] <| Impl.Create(check_all_songs))
            .WithCommand("check_artists", 
            Command.create "Scan artists for duplicates" [] <| Impl.Create(check_all_artists))
