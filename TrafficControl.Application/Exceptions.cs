namespace TrafficControl.Application;

public sealed class JunctionNotFoundException(string junctionId)
    : Exception($"Junction '{junctionId}' does not exist.");

public sealed class JunctionAlreadyExistsException(string junctionId)
    : Exception($"Junction '{junctionId}' already exists.");

public sealed class RequestValidationException(string message) : Exception(message);
