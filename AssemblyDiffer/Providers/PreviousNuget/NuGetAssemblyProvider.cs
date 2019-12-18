using Differ.Providers.NuGet;

namespace Differ.Providers.PreviousNuGet
{
	public class PreviousNuGetAssemblyProviderFactory : NuGetAssemblyProviderFactory
	{
		public PreviousNuGetAssemblyProviderFactory(PreviousNugetLocator installer) : base(installer) { }

		public override string Name { get; } = "previous-nuget";

		public override IAssemblyProvider Create(string[] command) =>
			new NuGetAssemblyProvider(Installer, command);
	}
}
