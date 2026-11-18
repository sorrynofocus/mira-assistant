using System.Text.Json;
using Mira.Core;

namespace Mira.Tests;

// Chunk 0 (M3): the shared preferences contract. Chunks A and B build on these rules.
public sealed class PreferenceContractTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void DefaultsAndJsonShapeMatchTheContract()
    {
        var json = JsonSerializer.Serialize(UserPreferences.Default, JsonSerializerOptions.Web);
        Assert.Equal("""{"theme":"mira-dark","fontSize":"default","inspectorOpenByDefault":false,"showInspectLinks":true}""", json);
    }

    [Theory]
    [InlineData("""{"theme":"sovereign"}""")]
    [InlineData("""{"theme":"system","fontSize":"large","inspectorOpenByDefault":true}""")]
    [InlineData("""{"fontSize":"small"}""")]
    [InlineData("""{"inspectorOpenByDefault":false}""")]
    [InlineData("""{"showInspectLinks":false}""")]
    public void ValidUpdatesParse(string body)
    {
        Assert.True(PreferenceRules.TryParseUpdate(Json(body), out var update, out var error));
        Assert.Null(error);
        Assert.False(update.IsEmpty);
    }

    [Theory]
    [InlineData("""{"theme":"neon"}""", "theme")]
    [InlineData("""{"theme":"MIRA-DARK"}""", "theme")]
    [InlineData("""{"theme":1}""", "theme")]
    [InlineData("""{"theme":null}""", "theme")]
    [InlineData("""{"fontSize":"huge"}""", "fontSize")]
    [InlineData("""{"inspectorOpenByDefault":"true"}""", "inspectorOpenByDefault")]
    [InlineData("""{"showInspectLinks":"false"}""", "showInspectLinks")]
    [InlineData("""{"ownerUserId":"x"}""", "")]
    [InlineData("""{"theme":"sovereign","theme":"mira-dark"}""", "theme")]
    [InlineData("""{}""", "")]
    [InlineData("""[]""", "")]
    public void InvalidUpdatesAreRejectedWithoutEchoingTheValue(string body, string key)
    {
        Assert.False(PreferenceRules.TryParseUpdate(Json(body), out _, out var error));
        Assert.Equal(key, error!.Key);
        Assert.DoesNotContain("neon", error.Message);
        Assert.DoesNotContain("huge", error.Message);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("ownerUserId")]
    [InlineData("Theme")]
    public void UnknownKeyNamesAreNeverEchoed(string name)
    {
        var body = Json(JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "x" }));
        Assert.False(PreferenceRules.TryParseUpdate(body, out _, out var error));
        Assert.Equal(new PreferenceValidationError("", "Unknown preference."), error);
    }

    [Fact]
    public void AVeryLongUnknownKeyNameIsNotEchoed()
    {
        var name = new string('k', 10_000);
        Assert.False(PreferenceRules.TryParseUpdate(Json($$"""{"{{name}}":1}"""), out _, out var error));
        Assert.Equal("", error!.Key);
        Assert.DoesNotContain("kkkk", error.Message);
    }

    [Fact]
    public void KnownKeyWithABadValueIsNamedWithoutTheValue()
    {
        Assert.False(PreferenceRules.TryParseUpdate(Json("""{"fontSize":"<b>x</b>"}"""), out _, out var error));
        Assert.Equal("fontSize", error!.Key);
        Assert.DoesNotContain("<b>", error.Message);
    }

    [Fact]
    public void PartialUpdateKeepsOtherValuesAndStoresStrings()
    {
        Assert.True(PreferenceRules.TryParseUpdate(Json("""{"inspectorOpenByDefault":true}"""), out var update, out _));
        var after = UserPreferences.Default.Apply(update);
        Assert.Equal(UserPreferences.Default with { InspectorOpenByDefault = true }, after);
        Assert.Equal(new Dictionary<string, string> { ["inspectorOpenByDefault"] = "true" }, update.ToStored());

        Assert.True(PreferenceRules.TryParseUpdate(Json("""{"showInspectLinks":false}"""), out var linksUpdate, out _));
        Assert.Equal(UserPreferences.Default with { ShowInspectLinks = false }, UserPreferences.Default.Apply(linksUpdate));
        Assert.Equal(new Dictionary<string, string> { ["showInspectLinks"] = "false" }, linksUpdate.ToStored());
    }

    [Fact]
    public void StoredRowsFallBackToDefaultsForUnknownOrInvalidValues()
    {
        var prefs = UserPreferences.FromStored(new Dictionary<string, string>
        {
            ["theme"] = "sovereign", ["fontSize"] = "gigantic", ["inspectorOpenByDefault"] = "yes", ["showInspectLinks"] = "yes", ["legacy"] = "x"
        });
        Assert.Equal(new UserPreferences("sovereign", "default", false, true), prefs);
        Assert.Equal(UserPreferences.Default, UserPreferences.FromStored(new Dictionary<string, string>()));
    }
}
