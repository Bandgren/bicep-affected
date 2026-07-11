import { defaultTags } from '../shared/tags.bicep'

param name string

resource app 'Microsoft.Web/sites@2022-03-01' = {
  name: name
  location: resourceGroup().location
  tags: defaultTags
}
