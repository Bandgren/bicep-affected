param storageAccountName string

module diagnostics 'diagnostics.bicep' = {
  name: 'diagnostics'
  params: {
    storageAccountName: storageAccountName
  }
}
