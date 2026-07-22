// Fixture: Controller con MediatR + Handler IRequestHandler<XCommand>.
// Verifica: aristas Sends (Controller→Command) y HandledBy (Command→Handler).
using MediatR;

namespace MyApp.Controllers;

public class UserController
{
    private readonly IMediator _mediator;

    public UserController(IMediator mediator) => _mediator = mediator;

    public void Create()
    {
        _mediator.Send(new CreateUserCommand());
    }
}

public class CreateUserCommand : IRequest<int> { }

public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, int>
{
    private readonly IUserService _userService;

    public CreateUserCommandHandler(IUserService userService) => _userService = userService;

    public int Handle(CreateUserCommand cmd) => _userService.Create();
}

public interface IUserService { int Create(); }
public class UserService : IUserService
{
    public int Create() => 0;
}
