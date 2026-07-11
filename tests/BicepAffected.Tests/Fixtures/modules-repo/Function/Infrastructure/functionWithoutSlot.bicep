import { resourceType } from '../../Config/Utils/types.bicep'

param actionGroup resourceType

module siteStoppedAlert '../Alerts/siteStopped.bicep' = {
  name: 'site-stopped'
  params: {
    actionGroup: actionGroup
  }
}

module registryAlert 'br/core:servicebus/alerts/queue:v0.5.1' = {
  name: 'registry-alert'
}
