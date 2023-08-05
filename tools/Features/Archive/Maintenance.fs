﻿namespace Backbeat.Features.Archive

open System
open System.Linq
open Percyqaz.Common
open Prelude.Backbeat.Archive

module Maintenance =

    let make_suggestion (flag: string) (id: SongId) (before: Song) (after: Song) : bool =
        let inline diff label a b = if a <> b then Logging.Info(sprintf "%s\n %A vvv\n %A" label a b)
        Logging.Info(sprintf "Backbot has a suggestion for %s" before.FormattedTitle)
        diff "Artists" before.Artists after.Artists
        diff "Performers" before.OtherArtists after.OtherArtists
        diff "Remixers" before.Remixers after.Remixers
        diff "Title" before.Title after.Title
        diff "Alt Titles" before.AlternativeTitles after.AlternativeTitles
        diff "Formatted title" before.FormattedTitle after.FormattedTitle
        diff "Tags" before.Tags after.Tags
        Logging.Info(sprintf "Reason: %s" flag)
        Logging.Info("\noptions ::\n 1 - Make this change\n 2 - Queue for manual review\n 3 - No correction needed")
        let mutable option_chosen = None
        while option_chosen.IsNone do
            match Console.ReadKey().Key with
            | ConsoleKey.D1 -> option_chosen <- Some true
            | ConsoleKey.D2 -> option_chosen <- Some false; Queue.append "song-review" id
            | ConsoleKey.D3 -> option_chosen <- Some false; Queue.append "song-ignore" id
            | _ -> ()
        option_chosen.Value

    let variations = 
        [|
            "tv size ver."; "tv ver."; "tv size"; "tv version"; "tv edit"; "tv-size"; "anime ver."; "op cut"; "op ver."
            "uncut ver."; "long ver.";"extended ver."; "extended mix"
            "cut ver.";  "short ver."; "short edit"
            "album ver"; "original mix"
        |]
    let song_version (song_id: SongId) (song: Song) =
        let remove_mixes_and_cuts (title: string) =
            let mutable title = title
            for v in variations do
                let i = title.ToLower().IndexOf(v)
                if i >= 0 then
                    let matched_v = title.Substring(i, v.Length)
                    title <- title.Replace("("+matched_v+")", "").Replace("["+matched_v+"]", "").Replace("-"+matched_v+"-", "").Replace("- "+matched_v+" -", "").Trim()
            title
        let suggestion = { song with Title = remove_mixes_and_cuts song.Title; AlternativeTitles = List.map remove_mixes_and_cuts song.AlternativeTitles }
        if suggestion <> song then
            if make_suggestion "SONGMIXES" song_id song suggestion then
                songs.[song_id] <- suggestion; save()
            
    let remix_regex = Text.RegularExpressions.Regex("\\((.*?) [rR]emix\\)$")
    let feature_separators = [|"FEAT."; "FT."; "Feat."; "Ft."; "featuring."; "feat."; "ft."|]
    let collab_separators = [|" x "; " X "; " / "; " VS "; " Vs "; " vs "; " vs. "; " Vs. "; " VS. "; "&"; ", and "; ","; " and "; "prod."|]
    let artist_separators = [|"&"; ", and "; ","; " and "|]
    let featuring_artists (song_id: SongId) (song: Song) =
        let suggestion =
            if song.Artists.Length > 1 || song.OtherArtists <> [] then song else

            let artists, features =
                let split : string array = song.Artists.[0].Split(feature_separators, StringSplitOptions.TrimEntries)
                let artists = split.[0].TrimEnd([|' '; '('|]).Split(collab_separators, StringSplitOptions.TrimEntries) |> List.ofArray
                let features = 
                    if split.Length > 1 then
                        split.[1].TrimEnd([|' '; ')'|]).Split(artist_separators, StringSplitOptions.TrimEntries) |> List.ofArray
                    else []
                artists, features

            let title, features2 =
                let split : string array = remix_regex.Replace(song.Title, "").Split(feature_separators, StringSplitOptions.TrimEntries)
                let title = if split.Length > 1 then split.[0].TrimEnd([|' '; '('; '['|]) else song.Title // todo a ft. b (a Remix)
                let features = 
                    if split.Length > 1 then
                        split.[1].TrimEnd([|' '; ')'; ']'|]).Split(artist_separators, StringSplitOptions.TrimEntries) |> List.ofArray
                    else []
                title, features

            { song with Title = title; OtherArtists = List.distinct (features @ features2); Artists = artists }
        if suggestion <> song then
            if make_suggestion "MULTIPLEARTISTS" song_id song suggestion then
                songs.[song_id] <- suggestion; save()

    let remixers_from_title (song_id: SongId) (song: Song) =
        let title_matches = remix_regex.Matches song.Title

        let suggestion = 
            if title_matches.Count = 1 then
                let remix_name = title_matches.[0].Groups.[1].Value
                let original_artist = song.Title.Replace(title_matches.First().Value, "").Trim()
                let remixer = if remix_name.Contains("'s") then remix_name.Split("'s").[0] else remix_name
                if song.Remixers = [] then
                    { song with Remixers = remixer.Split(collab_separators, StringSplitOptions.TrimEntries) |> List.ofArray }
                else song
            else song
        if suggestion <> song then
            if make_suggestion "REMIXINTITLE" song_id song suggestion then
                songs.[song_id] <- suggestion; save()

    let song_meta_checks_v2 (song_id: SongId) (song: Song) =
        featuring_artists song_id song
        song_version song_id song
        remixers_from_title song_id song

    let rehome_song_id (old_id: string, new_id: string) =
        for chart_id in charts.Keys do
            let chart = charts.[chart_id]
            if chart.SongId = old_id then
                charts.[chart_id] <- { chart with SongId = new_id }

    type Song_Deduplication = { Title: string; Artists: string list }
    let clean_duplicate_songs() =
        let mutable seen = Map.empty
        for id in songs.Keys |> Array.ofSeq do
            let song = songs.[id]
            let ded = { Title = song.Title.ToLower(); Artists = (song.Artists @ song.OtherArtists @ song.Remixers) |> List.map (fun s -> s.ToLower()) }
            match Map.tryFind ded seen with
            | Some existing ->
                Logging.Info(sprintf "%s is a duplicate of %s, merging" id existing)
                let existing_song = songs.[existing]
                songs.[existing] <- 
                    { existing_song with 
                        Source = Option.orElse song.Source existing_song.Source
                        Tags = List.distinct (existing_song.Tags @ song.Tags)
                        AlternativeTitles = List.distinct (existing_song.AlternativeTitles @ song.AlternativeTitles)
                    }
                songs.Remove id |> ignore
                rehome_song_id (id, existing)
            | None -> seen <- Map.add ded id seen
        save()

    let check_all_songs() =
        clean_duplicate_songs()
        let ignores = Queue.get "songs-ignore"
        let reviews = Queue.get "songs-review"
        for id in songs.Keys |> Seq.except ignores |> Seq.except reviews do
            song_meta_checks_v2 id songs.[id]

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
            
    let character_voice_regex = Text.RegularExpressions.Regex("[\\[\\(][cC][vV][.:\\-]?\\s?(.*)[\\]\\)]")
    let private check_all_artists_v2() =
        let map = artists.CreateMapping()
        let swap (s: string) =
            if map.ContainsKey(s.ToLower()) then 
                let replace = map.[s.ToLower()]
                replace
            else s
        
        let fix (artist: string) =
            let matches = character_voice_regex.Matches artist
            if matches.Count = 1 then
                let character_voice = matches.[0].Groups.[1].Value
                swap character_voice
            else swap artist
        
        for id in songs.Keys |> Array.ofSeq do
            let song = songs.[id]
            songs.[id] <-
                { song with 
                    Artists = List.map fix song.Artists
                    OtherArtists = List.map fix song.OtherArtists
                    Remixers = List.map fix song.Remixers
                }
        save()

    let check_all_artists() =
        check_all_artists_v2()
        let mutable checked_artists = Map.empty

        let distinct_artists = Queue.get "artists-distinct" |> Array.ofList

        let filter (name: string) =
            name.Length > 3 && String.forall Char.IsAscii name

        let check_artist (context: Song) (artist: string) =
            if checked_artists.ContainsKey artist then
                if checked_artists.[artist] = 2 && not (artists.Artists.ContainsKey artist) then
                    Console.WriteLine(sprintf "'%s' looks like a common artist, want to make a note to verify them? [1 for yes, 2 for no]" artist)
                    let mutable option_chosen = None
                    while option_chosen.IsNone do
                        match Console.ReadKey().Key with
                        | ConsoleKey.D1 -> option_chosen <- Some true
                        | ConsoleKey.D2 -> option_chosen <- Some false
                        | _ -> ()
                    if option_chosen.Value then
                        Queue.append "artists-verify" artist
                checked_artists <- checked_artists.Add (artist, checked_artists.[artist] + 1)
            else

            let b = List.ofSeq (artist.ToLower())
            let mutable closest_match = ""
            let mutable closest_match_v = artist.Length / 2
            for a in checked_artists.Keys |> Array.ofSeq do
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
                    | ConsoleKey.D1 -> rename_artist (artist, closest_match); checked_artists <- checked_artists.Remove closest_match; artist <- closest_match; option_chosen <- true
                    | ConsoleKey.D2 -> rename_artist (closest_match, artist); checked_artists <- checked_artists.Remove closest_match; option_chosen <- true
                    | ConsoleKey.D3 -> Queue.append "artists-distinct" (artist + "," + closest_match); option_chosen <- true
                    | _ -> ()
            checked_artists <- Map.add artist 1 checked_artists
                    
        for id in songs.Keys |> Array.ofSeq do
            let song = songs.[id]
            List.iter (check_artist song) song.Artists
            List.iter (check_artist song) song.OtherArtists
            List.iter (check_artist song) song.Remixers

    let check_all_ids() =
        for id in songs.Keys |> Array.ofSeq do
            let song = songs.[id]
            let new_id = (Collect.simplify_string (String.concat "" song.Artists)) + "/" + (Collect.simplify_string song.Title)
            let mutable i = 0
            while new_id <> id && songs.ContainsKey(if i > 0 then new_id + "-" + i.ToString() else new_id) do
                i <- i + 1
            let new_id = if i > 0 then new_id + "-" + i.ToString() else new_id
            if new_id <> id then 
                songs.Add(new_id, song)
                songs.Remove(id) |> ignore
                rehome_song_id (id, new_id)
        save()

    let verify_artist (name: string) =
        if artists.Artists.ContainsKey name then
            Logging.Warn("Already exists")
        else
            let is_japanese = name.Contains ' ' && Collect.romaji_regex.IsMatch(name.ToLower())
            artists.Artists.Add(name, { Alternatives = []; IsJapaneseFullName = is_japanese })
            Logging.Info(sprintf "Added %s, Is Japanese: %b" name is_japanese)
            save()

    open Prelude.Data.Charts.Caching

    let recache() =
        Cache.recache_service.RequestAsync(backbeat_cache) |> Async.RunSynchronously