param name string

// module ignoredByAst '../../infra/modules/ignored.bicep' = {}
var openApi = loadYamlContent('../../apis/employees/openapi.yaml')
var policy = loadTextContent('../../apis/employees/policy.xml')
var encodedPolicy = loadFileAsBase64('../../apis/employees/policy.bin')
var scriptFiles = loadDirectoryFileInfo('../../apis/employees/scripts', '*.ps1')

module app '../../infra/modules/app.bicep' = {
  name: 'app'
  params: {
    name: name
  }
}

output openApiLength int = length(string(openApi))
output policyLength int = length(policy)
output encodedPolicyLength int = length(encodedPolicy)
output scriptFileCount int = length(scriptFiles)
