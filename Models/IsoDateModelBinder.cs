using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CourtBook.Models;

// HTML date inputs submit Gregorian ISO dates, regardless of the server's calendar.
public sealed class IsoDateModelBinder : IModelBinderProvider, IModelBinder
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.Metadata.UnderlyingOrModelType == typeof(DateOnly) ? this : null;

    public Task BindModelAsync(ModelBindingContext context)
    {
        var value = context.ValueProvider.GetValue(context.ModelName);
        if (value == ValueProviderResult.None) return Task.CompletedTask;
        context.ModelState.SetModelValue(context.ModelName, value);
        if (string.IsNullOrWhiteSpace(value.FirstValue) && context.ModelMetadata.IsNullableValueType)
            context.Result = ModelBindingResult.Success(null);
        else if (DateOnly.TryParseExact(value.FirstValue, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var date))
            context.Result = ModelBindingResult.Success(date);
        else
            context.ModelState.TryAddModelError(context.ModelName, "วันที่ไม่ถูกต้อง กรุณาเลือกวันที่ใหม่");
        return Task.CompletedTask;
    }
}
