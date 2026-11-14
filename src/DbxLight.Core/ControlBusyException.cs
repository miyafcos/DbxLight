namespace DbxLight.Core;

public sealed class ControlBusyException() : TimeoutException("Another DbxLight operation is still running.");
