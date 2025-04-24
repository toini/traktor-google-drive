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
    private Collection? _collection;

    public CollectionService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public async Task<Collection> GetCollectionAsync()
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine($"Collection service start load {watch.Elapsed.TotalSeconds}s");
        if (_collection is not null) return _collection;

        var token = await _js.InvokeAsync<string>("sessionStorage.getItem", "access_token");
        Console.WriteLine($"Collection GetCollectionFileId {watch.Elapsed.TotalSeconds}s");
        var fileId = await GetCollectionFileId(token);

        Console.WriteLine($"Collection query collection file by id {watch.Elapsed.TotalSeconds}s");
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"Collection start parsing {watch.Elapsed.TotalSeconds}s");
        var parser = new TraktorNmlParser.NmlParser();
        _collection = parser.Load(content);

        Console.WriteLine($"Collection start parsing done {watch.Elapsed.TotalSeconds}s");
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
