using System.Collections.Generic;

namespace WebApp.Models;

public sealed class CityControlRequest
{
    public List<string>? Targets { get; set; }
    public int? Rate { get; set; }
}
