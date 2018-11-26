namespace Differ.Providers
{
	public interface IAssemblyProviderFactory
	{
		string Name { get; }
		string Format { get; }

		IAssemblyProvider Create(string[] command);
	}
}
