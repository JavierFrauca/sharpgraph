using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Corp.Api;

// Estilo CleanArchitecture v2: minimal APIs TIPADAS por grupos — el handler es un
// método estático de la clase (método-grupo como argumento) y la ruta, si la hay,
// va como SEGUNDO argumento. El endpoint debe adscribirse a la clase Orders.

public interface IOrderService { decimal Compute(int id); }

public sealed class OrderService : IOrderService
{
    private readonly IOrderRepo _repo;
    public OrderService(IOrderRepo repo) => _repo = repo;
    public decimal Compute(int id) => _repo.Find(id);
}

public interface IOrderRepo { int Find(int id); }

public sealed record ListOrdersQuery : IRequest<IReadOnlyList<string>>;
public sealed record CreateOrderCommand(string Sku) : IRequest<int>;

public sealed class ListOrdersHandler : IRequestHandler<ListOrdersQuery, IReadOnlyList<string>>
{
    private readonly IOrderService _svc;
    public ListOrdersHandler(IOrderService svc) => _svc = svc;

    public Task<IReadOnlyList<string>> Handle(ListOrdersQuery request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([]);
}

public sealed class Orders : IEndpointGroup
{
    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();

        groupBuilder.MapGet(GetOrders);
        groupBuilder.MapPost(CreateOrder);
        groupBuilder.MapPut(UpdateOrder, "{id}");
        groupBuilder.MapDelete(DeleteOrder, "{id}");
    }

    public static async Task GetOrders(ISender sender)
        => await sender.Send(new ListOrdersQuery());

    public static async Task CreateOrder(ISender sender)
        => await sender.Send(new CreateOrderCommand("sku"));

    public static async Task UpdateOrder(ISender sender, int id)
        => await sender.Send(new CreateOrderCommand("sku2"));

    public static Task DeleteOrder(ISender sender, int id) => Task.CompletedTask;
}
