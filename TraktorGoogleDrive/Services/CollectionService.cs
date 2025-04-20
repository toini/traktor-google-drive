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
        if (_collection is not null) return _collection;

        var token = await _js.InvokeAsync<string>("sessionStorage.getItem", "access_token");
        var fileId = await GetCollectionFileId(token);
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        var parser = new TraktorNmlParser.NmlParser();
        _collection = parser.Load(content);
        return _collection;
    }

    public async Task<Playlist?> GetPlaylistByUuidAsync(string uuid)
    {
        var collection = await GetCollectionAsync();
        return collection.Folders.SelectMany(f => f.Playlists).FirstOrDefault(p => p.Uuid == uuid);
    }

    //async Task<string> GetCollectionFileString(string token)
    //{
    //    var fileId = await GetCollectionFileId(token);
    //    if (fileId is null) throw new ApplicationException("Collection file not found");

    //    var result = await _http.GetFromJsonAsync<JsonElement>(
    //        $"https://www.googleapis.com/drive/v3/files/{fileId}?fields=id,name,mimeType,webContentLink&access_token={token}"
    //    );

    //    var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
    //    request.Headers.Authorization = new("Bearer", token);

    //    var response = await _http.SendAsync(request);
    //    response.EnsureSuccessStatusCode();
    //    return await response.Content.ReadAsStringAsync();
    //}

    //async Task<Stream> GetCollectionFile(string token)
    //{
    //    var fileId = await GetCollectionFileId(token);
    //    if (fileId is null) throw new ApplicationException("Collection file not found");

    //    var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
    //    request.Headers.Authorization = new("Bearer", token);

    //    var response = await _http.SendAsync(request);
    //    response.EnsureSuccessStatusCode();

    //    return await response.Content.ReadAsStreamAsync();
    //}

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
