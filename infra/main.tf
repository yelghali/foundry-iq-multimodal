data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 6
  lower   = true
  numeric = true
  special = false
  upper   = false
}

locals {
  suffix                 = random_string.suffix.result
  storage_account_name   = "stfdiq${local.suffix}"
  search_service_name    = "srch-fdiq-${local.suffix}"
  openai_account_name    = "oai-fdiq-${local.suffix}"
  aiservices_name        = "ais-fdiq-${local.suffix}"
  blob_container_name    = "enterprise-data"
  search_index_name      = "enterprise-multimodal"
  search_skillset_name   = "enterprise-multimodal-skillset"
  search_indexer_name    = "enterprise-multimodal-indexer"
  search_datasource_name = "enterprise-multimodal-blob"
}

resource "azurerm_resource_group" "lab" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_storage_account" "content" {
  name                      = local.storage_account_name
  resource_group_name       = azurerm_resource_group.lab.name
  location                  = azurerm_resource_group.lab.location
  account_tier              = "Standard"
  account_replication_type  = "LRS"
  min_tls_version           = "TLS1_2"
  shared_access_key_enabled = false
}

resource "azurerm_storage_container" "content" {
  name                  = local.blob_container_name
  storage_account_name  = azurerm_storage_account.content.name
  container_access_type = "private"
}

resource "azurerm_search_service" "search" {
  name                = local.search_service_name
  resource_group_name = azurerm_resource_group.lab.name
  location            = var.search_location
  sku                 = "basic"
  replica_count       = 1
  partition_count     = 1
  semantic_search_sku = "free"

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_cognitive_account" "openai" {
  name                          = local.openai_account_name
  resource_group_name           = azurerm_resource_group.lab.name
  location                      = azurerm_resource_group.lab.location
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = local.openai_account_name
  public_network_access_enabled = true
}

resource "azurerm_cognitive_account" "ai_services" {
  name                          = local.aiservices_name
  resource_group_name           = azurerm_resource_group.lab.name
  location                      = var.search_location
  kind                          = "CognitiveServices"
  sku_name                      = "S0"
  custom_subdomain_name         = local.aiservices_name
  public_network_access_enabled = true
}

resource "azurerm_cognitive_deployment" "chat" {
  name                 = var.chat_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = "gpt-4o"
    version = "2024-11-20"
  }

  scale {
    type     = "Standard"
    capacity = 10
  }
}

resource "azurerm_cognitive_deployment" "embedding" {
  name                 = var.embedding_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = "text-embedding-3-small"
    version = "1"
  }

  scale {
    type     = "Standard"
    capacity = 10
  }
}

resource "azurerm_role_assignment" "search_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_search_service.search.identity[0].principal_id
}

resource "azurerm_role_assignment" "search_storage_reader" {
  scope                = azurerm_storage_account.content.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_search_service.search.identity[0].principal_id
}

resource "azurerm_role_assignment" "search_ai_services_user" {
  scope                = azurerm_cognitive_account.ai_services.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_search_service.search.identity[0].principal_id
}

resource "azurerm_role_assignment" "user_storage_contributor" {
  scope                = azurerm_storage_account.content.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "user_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = data.azurerm_client_config.current.object_id
}
