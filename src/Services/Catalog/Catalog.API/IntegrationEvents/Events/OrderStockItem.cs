﻿namespace Microsoft.eShopOnDapr.Services.Catalog.API.IntegrationEvents.Events;

public record OrderStockItem(int ProductId, int Units);
