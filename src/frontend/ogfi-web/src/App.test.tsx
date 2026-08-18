import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { App } from './App'
import { apiClient } from './api/client'

const kilogramId = '10000000-0000-0000-0000-000000000002'
const taskId = 'd1000000-0000-0000-0000-000000000001'
const instanceId = 'd2000000-0000-0000-0000-000000000001'
const purchaseOrderId = 'd3000000-0000-0000-0000-000000000001'
const goodsReceiptId = 'e1000000-0000-0000-0000-000000000001'
const stockLocationId = 'e2000000-0000-0000-0000-000000000001'

const server = setupServer(
  http.get('*/api/catalog/uoms', () => HttpResponse.json([
    { id: kilogramId, code: 'KG', name: 'Kilogram', dimensionCode: 'MASS', precisionScale: 3 },
  ])),
  http.get('*/api/catalog/items', () => HttpResponse.json([
    { id: '91000000-0000-0000-0000-000000000001', code: 'TOMATO', name: 'Test Tomato', baseUomId: kilogramId, baseUomCode: 'KG', status: 'ACTIVE' },
  ])),
  http.get('*/api/workflow/approval-inbox', () => HttpResponse.json([
    { taskId, instanceId, purchaseOrderId, approvalRound: 1, subjectVersion: 2, purchaseOrderTotal: 5400, currency: 'PHP', outletId: '51111111-1111-1111-1111-111111111111', requesterUserId: '11111111-1111-1111-1111-111111111111', businessDate: '2026-08-18', createdAtUtc: '2026-08-18T01:00:00Z' },
  ])),
  http.get(`*/api/workflow/tasks/${taskId}`, () => HttpResponse.json({
    taskId,
    instanceId,
    definitionVersionId: 'd4000000-0000-0000-0000-000000000001',
    definitionVersion: 1,
    purchaseOrderId,
    approvalRound: 1,
    subjectVersion: 2,
    purchaseOrderTotal: 5400,
    currency: 'PHP',
    legalEntityId: '41111111-1111-1111-1111-111111111111',
    outletId: '51111111-1111-1111-1111-111111111111',
    requesterUserId: '11111111-1111-1111-1111-111111111111',
    businessDate: '2026-08-18',
    status: 'PENDING',
    createdAtUtc: '2026-08-18T01:00:00Z',
    completedAtUtc: null,
  })),
  http.post(`*/api/workflow/tasks/${taskId}/approve`, () => HttpResponse.json({
    id: 'd5000000-0000-0000-0000-000000000001',
    taskId,
    instanceId,
    decision: 'APPROVED',
    actorUserId: '11111111-1111-1111-1111-111111111111',
    decidedAtUtc: '2026-08-18T01:05:00Z',
  })),
  http.get('*/api/procurement/goods-receipts', () => HttpResponse.json([
    { id: goodsReceiptId, number: 'GR-20260818-TEST', purchaseOrderId, purchaseOrderNumberSnapshot: 'PO-20260818-TEST', supplierId: 'e3000000-0000-0000-0000-000000000001', supplierCodeSnapshot: 'SUP-E', supplierNameSnapshot: 'Supplier E', outletId: '51111111-1111-1111-1111-111111111111', stockLocationId, stockLocationCodeSnapshot: 'MAIN', currency: 'PHP', status: 'POSTED', businessDate: '2026-08-18', totalNetAmount: 200, createdAtUtc: '2026-08-18T02:00:00Z', postedAtUtc: '2026-08-18T02:05:00Z' },
  ])),
  http.get('*/api/procurement/purchase-orders', () => HttpResponse.json([
    { id: purchaseOrderId, number: 'PO-20260818-TEST', supplierId: 'e3000000-0000-0000-0000-000000000001', supplierCodeSnapshot: 'SUP-E', supplierNameSnapshot: 'Supplier E', legalEntityId: '41111111-1111-1111-1111-111111111111', outletId: '51111111-1111-1111-1111-111111111111', currency: 'PHP', status: 'APPROVED', businessDate: '2026-08-18', totalNetAmount: 1000, createdAtUtc: '2026-08-18T01:00:00Z' },
  ])),
  http.get('*/api/inventory/stock-locations', () => HttpResponse.json([
    { id: stockLocationId, outletId: '51111111-1111-1111-1111-111111111111', code: 'MAIN', name: 'Main Store', locationType: 'STORE', isActive: true },
  ])),
  http.get('*/api/inventory/stock-positions', () => HttpResponse.json([
    { id: 'e4000000-0000-0000-0000-000000000001', catalogItemId: '91000000-0000-0000-0000-000000000001', catalogItemCodeSnapshot: 'TOMATO', catalogItemNameSnapshot: 'Test Tomato', stockLocationId, stockLocationCodeSnapshot: 'MAIN', outletId: '51111111-1111-1111-1111-111111111111', baseUomId: kilogramId, baseUomCodeSnapshot: 'KG', quantityOnHand: 10, lastMovementOccurredAtUtc: '2026-08-18T02:05:00Z' },
  ])),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  cleanup()
  server.resetHandlers()
  window.history.pushState({}, '', '/overview')
})
afterAll(() => server.close())

function renderApp() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
}

describe('Batch E application workspace', () => {
  it('renders the routed operational shell without exposing a fake tenant selector', () => {
    window.history.pushState({}, '', '/overview')
    renderApp()
    expect(screen.getByText('OGFI Enterprise ERP')).toBeTruthy()
    expect(screen.getByText('Batch E · Stock Consequence')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Goods Receipts' })).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Stock Positions' })).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Inventory Movements' })).toBeTruthy()
    expect(screen.queryByLabelText(/tenant id/i)).toBeNull()
  })

  it('renders Catalog data through the typed API path and validates create form input', async () => {
    const direct = await apiClient.GET('/api/catalog/items', { params: { query: { limit: 100 } } })
    expect(direct.error).toBeUndefined()
    expect(direct.data?.[0]?.name).toBe('Test Tomato')
    window.history.pushState({}, '', '/overview')
    renderApp()
    const user = userEvent.setup()
    await user.click(screen.getByRole('link', { name: 'Catalog' }))
    expect(await screen.findByText('Test Tomato')).toBeTruthy()
    expect(screen.getByText('KG')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Create Item' }))
    expect(await screen.findByText('Code is required')).toBeTruthy()
    expect(screen.getByText('Name is required')).toBeTruthy()
    await user.type(screen.getByLabelText('Code'), 'NEW-ITEM')
    await user.type(screen.getByLabelText('Name'), 'New Item')
    expect(screen.getByDisplayValue('NEW-ITEM')).toBeTruthy()
  })

  it('uses the authenticated approval inbox and records the approval through the typed API path', async () => {
    window.history.pushState({}, '', '/overview')
    renderApp()
    const user = userEvent.setup()
    await user.click(screen.getByRole('link', { name: 'Approval Inbox' }))
    expect(await screen.findByText(purchaseOrderId)).toBeTruthy()
    await user.click(screen.getByRole('link', { name: 'Review' }))
    expect(await screen.findByText('Purchase Order Approval')).toBeTruthy()
    expect(screen.getByText(/PHP 5,400/)).toBeTruthy()
    await user.click(screen.getByRole('button', { name: 'Approve Purchase Order' }))
    expect(await screen.findByText(/Workflow decision recorded immutably as APPROVED/i)).toBeTruthy()
  })

  it('renders Goods Receipt and derived Stock Position data through generated typed API paths', async () => {
    const direct = await apiClient.GET('/api/inventory/stock-positions', { params: { query: { limit: 100 } } })
    expect(direct.error).toBeUndefined()
    expect(direct.data?.[0]?.quantityOnHand).toBe(10)
    window.history.pushState({}, '', '/overview')
    renderApp()
    const user = userEvent.setup()
    await user.click(screen.getByRole('link', { name: 'Goods Receipts' }))
    expect(await screen.findByText('GR-20260818-TEST')).toBeTruthy()
    expect(screen.getByText('POSTED')).toBeTruthy()
    await user.click(screen.getByRole('link', { name: 'Stock Positions' }))
    expect(await screen.findByText('TOMATO · Test Tomato')).toBeTruthy()
    expect(screen.getByText('10 KG')).toBeTruthy()
  })
})
