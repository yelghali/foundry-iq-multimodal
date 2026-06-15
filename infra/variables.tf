variable "subscription_id" {
  description = "Azure subscription id."
  type        = string
}

variable "tenant_id" {
  description = "Azure tenant id."
  type        = string
  default     = "5dc82be3-90ab-4f72-a0f2-b2557ba694e3"
}

variable "location" {
  description = "Azure region. Choose a region with Azure AI Search semantic ranker and Azure OpenAI model quota."
  type        = string
  default     = "eastus2"
}

variable "search_location" {
  description = "Azure AI Search region. Use a separate region if the main region has insufficient Search capacity."
  type        = string
  default     = "eastus"
}

variable "resource_group_name" {
  description = "Resource group for the lab."
  type        = string
  default     = "rg-foundry-iq-multimodal"
}

variable "chat_deployment_name" {
  description = "Azure OpenAI chat deployment used by the GenAI Prompt skill and Agent Framework."
  type        = string
  default     = "gpt-4o"
}

variable "embedding_deployment_name" {
  description = "Azure OpenAI embedding deployment used by Azure AI Search."
  type        = string
  default     = "text-embedding-3-small"
}
