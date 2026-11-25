using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

public sealed class ModelPickerTests
{
    private static LocalModel Model(string id, string name, LocalModelCaps caps, LocalModelSource source = LocalModelSource.File,
        LocalModelFit fit = LocalModelFit.Fits, string? size = null, string? quant = null, long bytes = 0) =>
        new(id, name, source, null, bytes, caps, null, size, quant, null, fit, null);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Reasons_follow_the_role()
    {
        var zeta = Model("a", "Zeta", LocalModelCaps.EditModel | LocalModelCaps.Predict);
        var chat = Model("b", "Qwen", LocalModelCaps.Chat | LocalModelCaps.Tools);
        Assert.Null(ModelPicker.UnsuitableFor(zeta, LocalAiRole.Predict));
        Assert.Equal("cant_chat", ModelPicker.UnsuitableFor(zeta, LocalAiRole.Chat));
        Assert.Equal("cant_predict", ModelPicker.UnsuitableFor(chat, LocalAiRole.Predict));
        Assert.Null(ModelPicker.UnsuitableFor(chat, LocalAiRole.Chat));
        Assert.Null(ModelPicker.UnsuitableFor(Model("c", "Fim", LocalModelCaps.Fim), LocalAiRole.Predict));
    }

    [Fact]
    public void Detail_and_badges()
    {
        Assert.Equal("9B · Q4_K_XL", ModelPicker.DetailOf(Model("a", "x", LocalModelCaps.None, size: "9B", quant: "Q4_K_XL")));
        Assert.Equal("2.0 GB", ModelPicker.DetailOf(Model("a", "x", LocalModelCaps.None, bytes: 2_000_000_000L)));
        Assert.Equal(["Predict", "FIM", "Chat", "Tools", "Thinking"],
            ModelPicker.BadgesOf(Model("a", "x", LocalModelCaps.Predict | LocalModelCaps.Fim | LocalModelCaps.Chat | LocalModelCaps.Tools | LocalModelCaps.Thinking)));
    }

    [Fact]
    public void Groups_in_order_with_assigned_first()
    {
        var file = Model("f", "File", LocalModelCaps.Chat);
        var ollama = Model("o", "Olla", LocalModelCaps.Chat, LocalModelSource.Ollama);
        var catalog = Model("c", "Cat", LocalModelCaps.Chat, LocalModelSource.Catalog);
        var assigned = Model("u", "Used", LocalModelCaps.Chat, LocalModelSource.Ollama);

        var rows = ModelPicker.Rows([catalog, ollama, file, assigned], LocalAiRole.Chat, new HashSet<string> { "u" });
        Assert.Equal(["u", "f", "o", "c"], rows.Select(row => row.Model.Id));
        Assert.Equal([ModelPickerGroup.Assigned, ModelPickerGroup.Computer, ModelPickerGroup.Ollama, ModelPickerGroup.Recommended], rows.Select(row => row.Group));
    }

    [Fact]
    public void Suitable_before_unsuitable_then_fit_then_name()
    {
        var rows = ModelPicker.Rows(
        [
            Model("1", "Alpha", LocalModelCaps.Predict),                        // can't chat
            Model("2", "Zulu", LocalModelCaps.Chat, fit: LocalModelFit.Tight),
            Model("3", "Mike", LocalModelCaps.Chat, fit: LocalModelFit.Fits),
            Model("4", "Bravo", LocalModelCaps.Chat, fit: LocalModelFit.Fits),
            Model("5", "Huge", LocalModelCaps.Chat, fit: LocalModelFit.TooBig),
        ], LocalAiRole.Chat, None);
        Assert.Equal(["4", "3", "2", "5", "1"], rows.Select(row => row.Model.Id));
        Assert.Equal("cant_chat", rows[^1].Unsuitable);
    }

    [Fact]
    public void Recommended_downloads_that_are_too_big_are_left_out()
    {
        var rows = ModelPicker.Rows(
        [
            Model("a", "Big", LocalModelCaps.Chat, LocalModelSource.Catalog, LocalModelFit.TooBig),
            Model("b", "Ok", LocalModelCaps.Chat, LocalModelSource.Catalog, LocalModelFit.Fits),
            Model("c", "Local big", LocalModelCaps.Chat, LocalModelSource.File, LocalModelFit.TooBig),
        ], LocalAiRole.Chat, None);
        Assert.Equal(["c", "b"], rows.Select(row => row.Model.Id));
    }

    [Fact]
    public void Search_text_holds_name_size_quant_and_badges()
    {
        var row = ModelPicker.Rows([Model("f1", "Qwen3.5", LocalModelCaps.Chat | LocalModelCaps.Tools, size: "9B", quant: "Q4_K_XL")], LocalAiRole.Chat, None)[0];
        Assert.Contains("Qwen3.5", row.SearchText);
        Assert.Contains("9B", row.SearchText);
        Assert.Contains("Q4_K_XL", row.SearchText);
        Assert.Contains("Tools", row.SearchText);
    }

    [Fact]
    public void Favourites_sort_first_in_their_group_among_those_that_suit()
    {
        var rows = ModelPicker.Rows(
        [
            Model("a", "Alpha", LocalModelCaps.Chat),
            Model("b", "Bravo", LocalModelCaps.Chat, fit: LocalModelFit.Tight),
            Model("c", "Cat", LocalModelCaps.Chat, LocalModelSource.Ollama),
            Model("d", "Delta", LocalModelCaps.Predict),   // can't chat, a favourite, still after those that can
        ], LocalAiRole.Chat, None, new HashSet<string> { "b", "d" });
        // Starred ones are the Favourites group, the first; in it those that suit the role come before those that don't
        Assert.Equal(["b", "d", "a", "c"], rows.Select(row => row.Model.Id));
        Assert.Equal([true, true, false, false], rows.Select(row => row.Favourite));
        Assert.Equal([ModelPickerGroup.Favourites, ModelPickerGroup.Favourites, ModelPickerGroup.Computer, ModelPickerGroup.Ollama], rows.Select(row => row.Group));
    }

    [Fact]
    public void Groups_shown_are_those_with_rows_in_order()
    {
        var rows = ModelPicker.Rows(
        [
            Model("c", "Cat", LocalModelCaps.Chat, LocalModelSource.Catalog),
            Model("o", "Olla", LocalModelCaps.Chat, LocalModelSource.Ollama),
            Model("u", "Used", LocalModelCaps.Chat),
        ], LocalAiRole.Chat, new HashSet<string> { "u" });
        Assert.Equal([ModelPickerGroup.Assigned, ModelPickerGroup.Ollama, ModelPickerGroup.Recommended], ModelPicker.GroupsShown(rows));
    }

    [Fact]
    public void Favourites_are_a_semicolon_list_toggled_and_cycled()
    {
        Assert.Equal(["a", "b"], LocalAiFavourites.Parse(" a ;b;A;;"));
        Assert.Equal("a;b;c", LocalAiFavourites.Toggle("a;b", "c"));
        Assert.Equal("b", LocalAiFavourites.Toggle("a;b", "A"));
        Assert.True(LocalAiFavourites.Contains("a;b", "B"));

        LocalModel[] models = [Model("a", "A", LocalModelCaps.Chat), Model("b", "B", LocalModelCaps.Predict), Model("c", "C", LocalModelCaps.Chat), Model("d", "D", LocalModelCaps.Chat, LocalModelSource.Catalog)];
        Assert.Null(LocalAiFavourites.Next("", models, "a"));
        Assert.Null(LocalAiFavourites.Next("b;d;zz", models, null));       // can't chat, not downloaded, not there
        Assert.Equal("a", LocalAiFavourites.Next("a;b;c", models, null)!.Id);
        Assert.Equal("c", LocalAiFavourites.Next("a;b;c", models, "a")!.Id);
        Assert.Equal("a", LocalAiFavourites.Next("a;b;c", models, "c")!.Id);
        Assert.Equal("a", LocalAiFavourites.Next("a;c", models, "not a favourite")!.Id);
    }

    [Fact]
    public void Fit_detail_gives_the_figures()
    {
        // Decimal GB everywhere, the way the rows and a file manager say them
        const long gb = 1_000_000_000L;
        LocalModel model = Model("a", "A", LocalModelCaps.Chat, bytes: gb * 15 / 2);
        Assert.Equal("7.5 GB weights + 1.1 GB context of 12 GB (fits)", LocalModelLibrary.FitDetail(model, 16384, 12 * gb));
        Assert.Equal("7.5 GB weights + 1.1 GB context of 9.5 GB (tight)", LocalModelLibrary.FitDetail(model, 16384, gb * 19 / 2));
        Assert.Equal("7.5 GB weights + 1.1 GB context of 8 GB (too big)", LocalModelLibrary.FitDetail(model, 16384, 8 * gb));
        Assert.Equal("7.5 GB weights + 1.1 GB context (card size unknown)", LocalModelLibrary.FitDetail(model, 16384, 0));
    }

    [Fact]
    public void The_row_and_the_fit_tooltip_say_the_same_unit()
    {
        LocalModel model = Model("a", "A", LocalModelCaps.Chat, bytes: 5_600_000_000);
        Assert.Equal("5.6 GB", ModelPicker.DetailOf(model));
        Assert.StartsWith("5.6 GB weights", LocalModelLibrary.FitDetail(model, 2048, 12_000_000_000));
    }

    [Fact]
    public void Badges_are_the_models_page_set_with_edit_model_said()
    {
        var zeta = Model("z", "Zeta", LocalModelCaps.Predict | LocalModelCaps.EditModel);
        Assert.Equal(["Predict", "Edit model"], ModelPicker.BadgesOf(zeta));
        Assert.Equal(["predict", "edit"], LocalModelBadges.Keys(zeta.Caps));
        Assert.Equal(["P:predict", "P:edit"], ModelPicker.BadgesOf(zeta, key => "P:" + key));
    }

    [Fact]
    public void Starred_models_make_the_first_group_once()
    {
        var rows = ModelPicker.Rows(
        [
            Model("a", "Alpha", LocalModelCaps.Chat),
            Model("b", "Bravo", LocalModelCaps.Chat, LocalModelSource.Ollama),
            Model("u", "Used", LocalModelCaps.Chat),
        ], LocalAiRole.Chat, new HashSet<string> { "u" }, new HashSet<string> { "b", "u" });
        Assert.Equal(["b", "u", "a"], rows.Select(row => row.Model.Id));
        Assert.Equal([ModelPickerGroup.Favourites, ModelPickerGroup.Favourites, ModelPickerGroup.Computer], rows.Select(row => row.Group));
        Assert.Equal([ModelPickerGroup.Favourites, ModelPickerGroup.Computer], ModelPicker.GroupsShown(rows));
    }

    [Fact]
    public void A_download_not_on_the_disk_is_unsuitable_with_its_reason()
    {
        var catalog = Model("c", "Qwen2.5-Coder", LocalModelCaps.Chat | LocalModelCaps.Predict, LocalModelSource.Catalog);
        Assert.Equal("not_downloaded", ModelPicker.UnsuitableFor(catalog, LocalAiRole.Chat));
        Assert.Equal("not_downloaded", ModelPicker.UnsuitableFor(catalog, LocalAiRole.Predict));
        Assert.Equal("not_downloaded", ModelPicker.Rows([catalog], LocalAiRole.Chat, None)[0].Unsuitable);
    }

    [Fact]
    public void A_catalog_entry_has_the_numbers_of_its_real_file()
    {
        // Layers and KV heads read off the files themselves: the context estimate, so the verdict, is the file's
        foreach (LlamaCppModel catalog in LlamaCppModels.All)
            Assert.True(catalog.Layers > 0 && catalog.KvHeads > 0, catalog.Id);

        LlamaCppModel mellum = LlamaCppModels.Find("mellum2.1")!;
        double fromCatalog = LocalModelLibrary.KvBytes(mellum.Layers, mellum.KvHeads, 32768);
        double fromFile = LocalModelLibrary.KvBytes(new GgufInfo("m.gguf", mellum.Size, "mellum", null, null, null, null, null, 131072, 28, 2048, 4, 64, null, false, false), 32768);
        Assert.Equal(fromFile, fromCatalog);
        Assert.Equal("9B", LlamaCppModels.Find("qwen3.5-9b")!.SizeLabel);
    }

    [Fact]
    public void Agent_choices_are_the_chat_models_on_disk_or_server()
    {
        var choices = ModelPicker.AgentChoices(
        [
            Model("a", "Zeta", LocalModelCaps.EditModel),
            Model("b", "Zed", LocalModelCaps.Chat),
            Model("c", "Download", LocalModelCaps.Chat, LocalModelSource.Catalog),
            Model("d", "Alpha", LocalModelCaps.Chat, LocalModelSource.Ollama),
        ]);
        Assert.Equal(["d", "b"], choices.Select(model => model.Id));
    }
}
