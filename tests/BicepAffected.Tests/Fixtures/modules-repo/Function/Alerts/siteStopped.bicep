import { resourceType } from '../../Config/Utils/types.bicep'

param actionGroup resourceType

resource alert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${actionGroup.name}-stopped'
  location: 'global'
  properties: {}
}
