// Fixture: atributos de routing de ASP.NET Core.
// Verifica: combinación [Route("api/[controller]")] + [HttpGet("{id}")] => /api/payroll/{id}
// y sustitución del token [controller] (PayrollController -> payroll).
using Microsoft.AspNetCore.Mvc;

namespace MyApp.Controllers;

[Route("api/[controller]")]
public class PayrollController : ControllerBase
{
    [HttpGet("{id}")]
    public object Get(int id) => null;

    [HttpPost("calculate")]
    public object Calculate() => null;
}
