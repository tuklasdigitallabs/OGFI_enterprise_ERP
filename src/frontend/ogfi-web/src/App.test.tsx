import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { App } from './App'

const kilogramId = '10000000-0000-0000-0000-000000000002'

const server = setupServer(
  http.get('*/api/catalog/uoms', () => HttpResponse.json([
    { id: kilogramId, code: 'KG', name: 'Kilogram', dimensionCode: 'MASS', precisionScale: 3 },
  ])),
  http.get('*/api/catalog/items', () => HttpResponse.json([
    { id: '91000000-0000-0000-0000-000000000001', code: 'TOMATO', name: 'Test Tomato', baseUomId: kilogramId, baseUomCode: 'KG', status: 'ACTIVE' },
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
  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  )
}

describe('Batch C application workspace', () => {
  it('renders the routed operational shell without exposing a fake tenant selector', () => {
    window.history.pushState({}, '', '/overview')
    renderApp()

    expect(screen.getByText('OGFI Enterprise ERP')).toBeTruthy()
    expect(screen.getByText('Batch C · Purchasing Master-Data Spine')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Purchase Orders' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Purchase Orders' })).toBeTruthy()
    expect(screen.queryByLabelText(/tenant id/i)).toBeNull()
  })

  it('renders Catalog data through the typed API path and validates create form input', async () => {
    window.history.pushState({}, '', '/overview')
    renderApp()

    const user = userEvent.setup()
    await user.click(screen.getByRole('link', { name: 'Catalog' }))

    expect(await screen.findByText('Test Tomato')).toBeTruthy()
    expect(screen.getByText('KG')).toBeTruthy()

    const submit = screen.getByRole('button', { name: 'Create Item' })
    fireEvent.click(submit)

    expect(await screen.findByText('Code is required')).toBeTruthy()
    expect(screen.getByText('Name is required')).toBeTruthy()

    await user.type(screen.getByLabelText('Code'), 'NEW-ITEM')
    await user.type(screen.getByLabelText('Name'), 'New Item')
    expect(screen.getByDisplayValue('NEW-ITEM')).toBeTruthy()
  })
})
