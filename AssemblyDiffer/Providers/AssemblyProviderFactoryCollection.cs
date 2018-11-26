using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Differ.Providers
{
	public class AssemblyProviderFactoryCollection : IEnumerable<IAssemblyProviderFactory>
	{
		private readonly AssemblyFileInfoProviderCollection _providers;

		public AssemblyProviderFactoryCollection(params IAssemblyProviderFactory[] providerFactories) =>
			_providers = new AssemblyFileInfoProviderCollection(providerFactories);

		public IAssemblyProvider GetProvider(string command)
		{
			if (command == null)
				throw new ArgumentNullException(nameof(command));

			var commandParts = command.Split('|');
			var providerName = commandParts[0];

			if (!_providers.Contains(providerName))
				throw new Exception($"No provider factory for {providerName}");

			var provider = _providers[providerName].Create(commandParts.Skip(1).ToArray());
			return provider;
		}

		private class AssemblyFileInfoProviderCollection : KeyedCollection<string, IAssemblyProviderFactory>
		{
			public AssemblyFileInfoProviderCollection(IEnumerable<IAssemblyProviderFactory> providerFactories)
			{
				if (providerFactories == null)
					throw new ArgumentNullException(nameof(providerFactories));

				foreach (var factory in providerFactories)
					this.Add(factory);
			}

			protected override string GetKeyForItem(IAssemblyProviderFactory item) => item.Name;
		}

		public IEnumerator<IAssemblyProviderFactory> GetEnumerator() => _providers.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
