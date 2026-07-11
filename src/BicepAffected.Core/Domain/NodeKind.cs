namespace BicepAffected.Core.Domain;

public enum NodeKind
{
    Entrypoint,
    PublishableModule,
    Helper,
    UnknownBicepFile,
    ContentFile,
    ConfigFile
}
