// Fixture: Minimal API con app.MapGet + lambda tipada.
// Verifica: endpoint sintético "GET /users" + arista Call desde el handler hasta UserService.Get.
using Microsoft.AspNetCore.Builder;

namespace MyApp
{
    public static class Endpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/users", (UserService svc, int id) => svc.Get(id));
        }
    }

    public class UserService
    {
        public object Get(int id) => null;
    }
}
