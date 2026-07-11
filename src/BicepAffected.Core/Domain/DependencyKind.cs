namespace BicepAffected.Core.Domain;

public enum DependencyKind
{
    LocalModule,
    CompileTimeImport,
    ContentLoad,
    DirectoryContent,
    ParameterFile,
    GlobalConfig,
    ExternalModule
}
