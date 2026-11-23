using System.Globalization;
using CourtBook.Models;

// Run the real application with the same calendar used on Thai Windows.
var culture = CultureInfo.GetCultureInfo("th-TH");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;
var entryPoint = typeof(BookingForm).Assembly.EntryPoint!;
await (Task)entryPoint.Invoke(null, new object[] { args.Concat(new[] { "--applicationName", "CourtBook" }).ToArray() })!;
