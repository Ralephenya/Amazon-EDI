using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jumbo.AmazonEdi.Core.Models;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

/// <summary>
/// Guards our wire models against Amazon's published Vendor Invoices model, vendored at
/// Schema/vendorInvoices.json from amzn/selling-partner-api-models. If Amazon renames a field or
/// changes an enum, this fails the build rather than production.
///
/// Refresh the vendored copy with:
///   curl -o tests/Jumbo.AmazonEdi.Tests/Schema/vendorInvoices.json \
///     https://raw.githubusercontent.com/amzn/selling-partner-api-models/main/models/vendor-invoices-api-model/vendorInvoices.json
/// </summary>
public class InvoicePayloadContractTests
{
    private static readonly JsonDocument Model = LoadModel();

    public static TheoryData<Type, string> MappedTypes() => new()
    {
        { typeof(Invoice), "Invoice" },
        { typeof(InvoiceItem), "InvoiceItem" },
        { typeof(PartyIdentification), "PartyIdentification" },
        { typeof(Address), "Address" },
        { typeof(TaxRegistrationDetails), "TaxRegistrationDetails" },
        { typeof(TaxDetails), "TaxDetails" },
        { typeof(Money), "Money" },
        { typeof(ItemQuantity), "ItemQuantity" },
        { typeof(ChargeDetails), "ChargeDetails" },
        { typeof(AllowanceDetails), "AllowanceDetails" },
        { typeof(AdditionalDetails), "AdditionalDetails" },
        { typeof(PaymentTerms), "PaymentTerms" },
        { typeof(CreditNoteDetails), "CreditNoteDetails" },
    };

    [Theory]
    [MemberData(nameof(MappedTypes))]
    public void Every_property_we_send_exists_in_amazons_model(Type type, string definitionName)
    {
        var amazonProperties = PropertyNames(definitionName);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var wireName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            Assert.True(wireName is not null, $"{type.Name}.{property.Name} has no [JsonPropertyName].");
            Assert.True(
                amazonProperties.Contains(wireName!),
                $"{type.Name}.{property.Name} serializes as '{wireName}', which is not in Amazon's {definitionName} definition.");
        }
    }

    [Theory]
    [MemberData(nameof(MappedTypes))]
    public void Every_required_field_in_amazons_model_is_present_on_ours(Type type, string definitionName)
    {
        var ourProperties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var required in RequiredPropertyNames(definitionName))
        {
            Assert.True(
                ourProperties.Contains(required),
                $"Amazon requires '{required}' on {definitionName}, but {type.Name} does not send it.");
        }
    }

    [Fact]
    public void The_endpoint_path_is_still_the_one_we_call()
    {
        Assert.True(Model.RootElement.GetProperty("paths").TryGetProperty("/vendor/payments/v1/invoices", out _));
    }

    [Theory]
    [InlineData("Invoice", "invoiceType", new[] { InvoiceTypes.Invoice, InvoiceTypes.CreditNote })]
    [InlineData("TaxDetails", "taxType", new[] { TaxTypes.Vat, TaxTypes.Gst, TaxTypes.DomesticVat })]
    [InlineData("ItemQuantity", "unitOfMeasure", new[] { UnitsOfMeasure.Cases, UnitsOfMeasure.Eaches })]
    public void The_enum_values_we_use_are_still_accepted(string definitionName, string propertyName, string[] valuesWeUse)
    {
        var allowed = Definition(definitionName)
            .GetProperty("properties")
            .GetProperty(propertyName)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var value in valuesWeUse)
        {
            Assert.True(allowed.Contains(value), $"'{value}' is no longer a valid {definitionName}.{propertyName}.");
        }
    }

    private static JsonElement Definition(string name) =>
        Model.RootElement.GetProperty("definitions").GetProperty(name);

    private static HashSet<string> PropertyNames(string definitionName) =>
        Definition(definitionName)
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> RequiredPropertyNames(string definitionName)
    {
        var definition = Definition(definitionName);
        if (!definition.TryGetProperty("required", out var required))
        {
            yield break;
        }

        foreach (var value in required.EnumerateArray())
        {
            var name = value.GetString();
            if (name is not null)
            {
                yield return name;
            }
        }
    }

    private static JsonDocument LoadModel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Schema", "vendorInvoices.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
