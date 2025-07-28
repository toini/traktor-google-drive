using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.JSInterop;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

public class CollectionService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private Collection? _collection = null;

    public CollectionService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public async Task<Collection> GetCollectionAsync()
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine($"[CollectionService] Start load {watch.Elapsed.TotalSeconds}s");
        if (_collection is not null)
        {
            Console.WriteLine($"[CollectionService] Returning cached collection {watch.Elapsed.TotalSeconds}s");
            return _collection;
        }
        var token = await _js.InvokeAsync<string>("sessionStorage.getItem", "access_token");
        Console.WriteLine($"[CollectionService] Got token {watch.Elapsed.TotalSeconds}s");
        var fileId = await GetCollectionFileId(token);
        Console.WriteLine($"[CollectionService] Got fileId {fileId} {watch.Elapsed.TotalSeconds}s");
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[CollectionService] Downloaded content {content.Length} bytes {watch.Elapsed.TotalSeconds}s");
        var parser = new TraktorNmlParser.NmlParser();
        _collection = parser.Load(content);
        Console.WriteLine($"[CollectionService] Parsed collection {watch.Elapsed.TotalSeconds}s");
        return _collection;
    }

    public async Task<Playlist?> GetPlaylistByUuid(string uuid)
    {
        var collection = await GetCollectionAsync();
        return collection.Folders.SelectMany(f => f.Playlists).FirstOrDefault(p => p.Uuid == uuid);
    }

    async Task<string?> GetCollectionFileId(string token)
    {
        var musiikkiId = await FindChildId("root", "Musiikki", token);
        if (musiikkiId is null) return null;

        var nativeInstrumentsId = await FindChildId(musiikkiId, "Native Instruments", token);
        if (nativeInstrumentsId is null) return null;

        return await FindChildId(nativeInstrumentsId, "collection.nml", token);
    }

    async Task<string?> FindChildId(string parentId, string name, string token)
    {
        var url = $"https://www.googleapis.com/drive/v3/files?q='{parentId}'+in+parents+and+name='{name}'&fields=files(id)&access_token={token}";
        var result = await _http.GetFromJsonAsync<JsonElement>(url);
        return result.GetProperty("files").EnumerateArray().FirstOrDefault().GetProperty("id").GetString();
    }
}
