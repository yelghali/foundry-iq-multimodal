output "resource_group_name" {
  value = azurerm_resource_group.lab.name
}

output "storage_account_name" {
  value = azurerm_storage_account.content.name
}

output "storage_container_name" {
  value = azurerm_storage_container.content.name
}

output "storage_account_id" {
  value = azurerm_storage_account.content.id
}

output "storage_blob_endpoint" {
  value = azurerm_storage_account.content.primary_blob_endpoint
}

output "search_service_name" {
  value = azurerm_search_service.search.name
}

output "search_endpoint" {
  value = "https://${azurerm_search_service.search.name}.search.windows.net"
}

output "search_admin_key" {
  value     = azurerm_search_service.search.primary_key
  sensitive = true
}

output "search_index_name" {
  value = local.search_index_name
}

output "search_skillset_name" {
  value = local.search_skillset_name
}

output "search_indexer_name" {
  value = local.search_indexer_name
}

output "search_datasource_name" {
  value = local.search_datasource_name
}

output "openai_endpoint" {
  value = azurerm_cognitive_account.openai.endpoint
}

output "chat_deployment_name" {
  value = azurerm_cognitive_deployment.chat.name
}

output "embedding_deployment_name" {
  value = azurerm_cognitive_deployment.embedding.name
}

output "ai_services_key" {
  value     = azurerm_cognitive_account.ai_services.primary_access_key
  sensitive = true
}

output "ai_services_endpoint" {
  value = azurerm_cognitive_account.ai_services.endpoint
}
