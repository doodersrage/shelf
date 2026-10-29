using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class CustomFieldsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<BookResponse> CreateAsync(HttpClient client, string title) =>
        (await (await client.PostAsJsonAsync("/books", new CreateBookRequest(title, "Someone", BookStatus.Want, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;

    private static async Task<CustomFieldResponse> FieldAsync(HttpClient client, string name, FieldKind kind)
    {
        var response = await client.PostAsJsonAsync("/books/fields", new CustomFieldRequest(name, kind), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomFieldResponse>(JsonOptions))!;
    }

    // A reader's fields: named once each in any case, renamed but not changed in kind, and seen by no one else.
    [Fact]
    public async Task A_reader_makes_names_and_removes_fields_of_their_own()
    {
        var reader = await factory.SignUpAsync("Field Maker");
        var box = await FieldAsync(reader, "Box", FieldKind.Text);
        await FieldAsync(reader, "Price", FieldKind.Number);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/fields", new CustomFieldRequest("  box "), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/books/fields", new CustomFieldRequest(""), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/fields/{box.Id}", new CustomFieldRequest("Packing box"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PutAsJsonAsync($"/books/fields/{box.Id}", new CustomFieldRequest("PRICE"), JsonOptions)).StatusCode);
        var fields = (await reader.GetFromJsonAsync<CustomFieldResponse[]>("/books/fields", JsonOptions))!;
        Assert.Equal(["Packing box", "Price"], fields.Select(field => field.Name));
        Assert.Equal(FieldKind.Text, fields[0].Kind);

        var stranger = await factory.SignUpAsync("Field Stranger");
        Assert.Empty((await stranger.GetFromJsonAsync<CustomFieldResponse[]>("/books/fields", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/books/fields/{box.Id}", new CustomFieldRequest("Mine now"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/books/fields/{box.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/books/fields/{box.Id}")).StatusCode);
        Assert.Equal(["Price"], (await reader.GetFromJsonAsync<CustomFieldResponse[]>("/books/fields", JsonOptions))!.Select(field => field.Name));
    }

    // Values are checked against their field's kind and kept one way; one wrong keeps none of them. A blank clears.
    // The library finds books by them, by a field's value or by anything in it, and another reader can neither see
    // nor set them.
    [Fact]
    public async Task Values_are_checked_kept_one_way_found_and_private()
    {
        var reader = await factory.SignUpAsync("Field Filler");
        var from = await FieldAsync(reader, "Bought from", FieldKind.Text);
        var price = await FieldAsync(reader, "Price", FieldKind.Number);
        var when = await FieldAsync(reader, "Packed on", FieldKind.Date);
        var signed = await FieldAsync(reader, "Dedicated", FieldKind.YesNo);
        var book = await CreateAsync(reader, "A Filled Book");
        var other = await CreateAsync(reader, "An Empty Book");

        var wrong = await reader.PutAsJsonAsync($"/books/{book.Id}/fields", new CustomValuesRequest(new() { [from.Id] = "Oxfam", [price.Id] = "lots" }), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("Price", await wrong.Content.ReadAsStringAsync());
        Assert.All((await reader.GetFromJsonAsync<CustomValueResponse[]>($"/books/{book.Id}/fields", JsonOptions))!, value => Assert.Null(value.Value));

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/{book.Id}/fields",
            new CustomValuesRequest(new() { [from.Id] = " Oxfam Books ", [price.Id] = "3.50", [when.Id] = "2026-10-29", [signed.Id] = "Yes" }), JsonOptions)).StatusCode);
        var values = (await reader.GetFromJsonAsync<CustomValueResponse[]>($"/books/{book.Id}/fields", JsonOptions))!.ToDictionary(value => value.Name, value => value.Value);
        Assert.Equal("Oxfam Books", values["Bought from"]);
        Assert.Equal("3.50", values["Price"]);
        Assert.Equal("2026-10-29", values["Packed on"]);
        Assert.Equal("yes", values["Dedicated"]);

        var found = (await reader.GetFromJsonAsync<BookResponse[]>("/books?q=oxfam", JsonOptions))!;
        Assert.Equal([book.Id], found.Select(item => item.Id));
        Assert.Equal([book.Id], (await reader.GetFromJsonAsync<BookResponse[]>($"/books?field={signed.Id}&value=YES", JsonOptions))!.Select(item => item.Id));
        Assert.Equal([book.Id], (await reader.GetFromJsonAsync<BookResponse[]>($"/books?field={price.Id}", JsonOptions))!.Select(item => item.Id));
        Assert.Contains("A Filled Book", await reader.GetStringAsync($"/library?field={from.Id}&value=Oxfam%20Books"));
        Assert.DoesNotContain(other.Title, await reader.GetStringAsync($"/library?field={from.Id}&value=Oxfam%20Books"));

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PutAsJsonAsync($"/books/{book.Id}/fields", new CustomValuesRequest(new() { [from.Id] = "" }), JsonOptions)).StatusCode);
        Assert.Null((await reader.GetFromJsonAsync<CustomValueResponse[]>($"/books/{book.Id}/fields", JsonOptions))!.Single(value => value.FieldId == from.Id).Value);

        var stranger = await factory.SignUpAsync("Value Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/fields")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/books/{book.Id}/fields", new CustomValuesRequest(new() { [price.Id] = "1" }), JsonOptions)).StatusCode);
        var theirs = await CreateAsync(stranger, "The Stranger's Book");
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PutAsJsonAsync($"/books/{theirs.Id}/fields", new CustomValuesRequest(new() { [price.Id] = "1" }), JsonOptions)).StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<BookResponse[]>("/books?q=oxfam", JsonOptions))!);
    }

    [Theory]
    [InlineData(FieldKind.Number, "1e3", "1000")]
    [InlineData(FieldKind.Number, "-2.5", "-2.5")]
    [InlineData(FieldKind.Date, "2026-02-30", null)]
    [InlineData(FieldKind.YesNo, "no", "no")]
    [InlineData(FieldKind.YesNo, "maybe", null)]
    [InlineData(FieldKind.Text, "   ", null)]
    public void A_value_is_kept_in_one_form(FieldKind kind, string given, string? kept) =>
        Assert.Equal(kept, CustomFields.Normalize(kind, given).Value);

    // The fields and their values go out in the export and the spreadsheet, and come back in on another shelf.
    [Fact]
    public async Task Fields_go_out_in_exports_and_come_back_in()
    {
        var reader = await factory.SignUpAsync("Field Exporter");
        var shelfMark = await FieldAsync(reader, "Shelf mark", FieldKind.Text);
        var book = await CreateAsync(reader, "Exported With Fields");
        await reader.PutAsJsonAsync($"/books/{book.Id}/fields", new CustomValuesRequest(new() { [shelfMark.Id] = "B-12" }), JsonOptions);

        var export = (await reader.GetFromJsonAsync<LibraryExport>("/books/export", JsonOptions))!;
        Assert.Equal("Shelf mark", Assert.Single(export.Fields!).Name);
        var value = Assert.Single(export.FieldValues!);
        Assert.Equal(("Shelf mark", "B-12"), (value.Field, value.Value));
        Assert.Equal("Exported With Fields", export.Books[value.Book].Title);

        var csv = PortableExport.Csv([new Book { Id = 7, Title = "T", Author = "A" }], [new CustomField { Id = 3, Name = "Shelf mark" }], new Dictionary<(int, int), string> { [(7, 3)] = "B-12" });
        Assert.EndsWith(",Shelf mark", csv.Split('\n')[0].TrimEnd('\r'));
        Assert.EndsWith(",B-12", csv.Split('\n')[1].TrimEnd('\r'));

        var elsewhere = await factory.SignUpAsync("Field Importer");
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.PostAsJsonAsync("/books/import", export, JsonOptions)).StatusCode);
        var field = Assert.Single((await elsewhere.GetFromJsonAsync<CustomFieldResponse[]>("/books/fields", JsonOptions))!);
        var imported = Assert.Single((await elsewhere.GetFromJsonAsync<BookResponse[]>("/books", JsonOptions))!);
        Assert.Equal("B-12", (await elsewhere.GetFromJsonAsync<CustomValueResponse[]>($"/books/{imported.Id}/fields", JsonOptions))!.Single(item => item.FieldId == field.Id).Value);
    }

    // Two entries merged: a value only the other had comes across; the kept book's own wins where both have one.
    [Fact]
    public async Task A_merge_keeps_values_the_kept_book_lacked()
    {
        var reader = await factory.SignUpAsync("Field Merger");
        var box = await FieldAsync(reader, "Box", FieldKind.Text);
        var price = await FieldAsync(reader, "Price", FieldKind.Number);
        var kept = await CreateAsync(reader, "Merged Book");
        var other = await CreateAsync(reader, "Merged Book Again");
        await reader.PutAsJsonAsync($"/books/{kept.Id}/fields", new CustomValuesRequest(new() { [box.Id] = "Kept box" }), JsonOptions);
        await reader.PutAsJsonAsync($"/books/{other.Id}/fields", new CustomValuesRequest(new() { [box.Id] = "Other box", [price.Id] = "4" }), JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync($"/books/{kept.Id}/merge", new MergeRequest(other.Id), JsonOptions)).StatusCode);
        var values = (await reader.GetFromJsonAsync<CustomValueResponse[]>($"/books/{kept.Id}/fields", JsonOptions))!.ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal("Kept box", values["Box"]);
        Assert.Equal("4", values["Price"]);
    }
}
