namespace Anka.App.Abstractions;
public interface IRequestHandler<TRequest, TResponse> { Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken); }
