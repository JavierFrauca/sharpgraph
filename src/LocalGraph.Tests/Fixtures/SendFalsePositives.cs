// Fixture: métodos llamados Send/Dispatch sobre receptores que NO son un bus MediatR.
// Verifica: NO se generan aristas Sends espurias.
namespace MyApp;

public class EmailGateway
{
    // receiver type es EmailGateway, no un IBus/IMediator -> NO debe contar como Sends
    public void Dispatch(EmailMessage msg) { }
}

public class SmtpClient
{
    public void Send(EmailMessage msg) { }
}

public class EmailMessage { }

public class Caller
{
    private readonly EmailGateway _gateway;
    private readonly SmtpClient _smtp;

    public Caller(EmailGateway gateway, SmtpClient smtp)
    {
        _gateway = gateway;
        _smtp = smtp;
    }

    public void Run(EmailMessage msg)
    {
        _gateway.Dispatch(msg);
        _smtp.Send(msg);
    }
}
