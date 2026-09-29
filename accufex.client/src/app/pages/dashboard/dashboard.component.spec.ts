import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DashboardComponent, DashboardSummary } from './dashboard.component';
import { StatementService } from '../../services/statement.service';
import { AuthService } from '../../services/auth.service';
import { HttpClient } from '@angular/common/http';
import { RouterModule } from '@angular/router';
import { of, throwError } from 'rxjs';
import { vi, describe, it, expect, beforeEach } from 'vitest';

describe('DashboardComponent (Phase 7 / Step 1)', () => {
  let component: DashboardComponent;
  let fixture: ComponentFixture<DashboardComponent>;
  let mockHttp: { get: ReturnType<typeof vi.fn> };
  let mockStatementService: { downloadStatementFile: ReturnType<typeof vi.fn> };
  let mockAuthService: { currentUser: { workspaceName: string } | null };

  const sampleSummary: DashboardSummary = {
    status: 'Online / Connected',
    database: 'Accufex',
    server: 'SQL Server Connected',
    canConnect: true,
    totalStatements: 4,
    completedStatements: 2,
    statementsProcessed: 2,
    processingStatements: 1,
    failedStatements: 1,
    totalTransactions: 3188,
    clientProfiles: 1,
    recentStatements: [
      {
        id: '11111111-1111-1111-1111-111111111111',
        originalFileName: 'HDFC_0371_Statement.pdf',
        bankName: 'HDFC Bank',
        uploadedAt: '2026-09-01T10:30:00Z',
        status: 'Completed',
        processingStatus: 2,
        transactionCount: 1801,
        fileSizeBytes: 587142,
        fileSizeFormatted: '573.38 KB'
      },
      {
        id: '22222222-2222-2222-2222-222222222222',
        originalFileName: 'YES_Bank_Passbook.pdf',
        bankName: 'YES BANK',
        uploadedAt: '2026-09-02T11:00:00Z',
        status: 'Completed',
        processingStatus: 2,
        transactionCount: 92,
        fileSizeBytes: 154200,
        fileSizeFormatted: '150.59 KB'
      },
      {
        id: '33333333-3333-3333-3333-333333333333',
        originalFileName: 'Axis_Current_Account.pdf',
        bankName: 'Axis Bank',
        uploadedAt: '2026-09-03T12:00:00Z',
        status: 'ReadyForProcessing',
        processingStatus: 0,
        transactionCount: 0,
        fileSizeBytes: 204800,
        fileSizeFormatted: '200.00 KB'
      },
      {
        id: '44444444-4444-4444-4444-444444444444',
        originalFileName: 'Corrupted_Scan.pdf',
        bankName: 'Pending',
        uploadedAt: '2026-09-04T13:00:00Z',
        status: 'Failed',
        processingStatus: 3,
        transactionCount: 0,
        fileSizeBytes: 51200,
        fileSizeFormatted: '50.00 KB'
      }
    ]
  };

  beforeEach(async () => {
    mockHttp = {
      get: vi.fn().mockReturnValue(of(sampleSummary))
    };

    mockStatementService = {
      downloadStatementFile: vi.fn()
    };

    mockAuthService = {
      currentUser: { workspaceName: 'Test Accounting Corp' }
    };

    await TestBed.configureTestingModule({
      declarations: [DashboardComponent],
      imports: [RouterModule.forRoot([])],
      providers: [
        { provide: HttpClient, useValue: mockHttp },
        { provide: StatementService, useValue: mockStatementService },
        { provide: AuthService, useValue: mockAuthService }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DashboardComponent);
    component = fixture.componentInstance;
  });

  it('1. should create the component and initialize with workspace name', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
    expect(component.userWorkspaceName).toBe('Test Accounting Corp');
  });

  it('2. should fetch dashboard summary and render all 4 mandatory summary cards plus transactions', () => {
    fixture.detectChanges();

    expect(mockHttp.get).toHaveBeenCalledWith('/api/dashboard/summary');
    expect(component.loading).toBe(false);
    expect(component.stats.totalStatements).toBe(4);
    expect(component.stats.completedStatements).toBe(2);
    expect(component.stats.processingStatements).toBe(1);
    expect(component.stats.failedStatements).toBe(1);
    expect(component.stats.totalTransactions).toBe(3188);

    const compiled = fixture.nativeElement as HTMLElement;
    const statCards = compiled.querySelectorAll('.stat-card');
    expect(statCards.length).toBe(5);

    // Verify card values in DOM
    const textContent = compiled.textContent || '';
    expect(textContent).toContain('Total Statements');
    expect(textContent).toContain('Completed Conversions');
    expect(textContent).toContain('In Progress');
    expect(textContent).toContain('Needs Attention');
  });

  it('3. should render recent statements table with Bank, upload date, transaction count and status', () => {
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const rows = compiled.querySelectorAll('.statements-table tbody tr');
    expect(rows.length).toBe(4);

    const firstRowText = rows[0].textContent || '';
    expect(firstRowText).toContain('HDFC_0371_Statement.pdf');
    expect(firstRowText).toContain('HDFC Bank');
    expect(firstRowText).toContain('1,801 rows');
    expect(firstRowText).toContain('Completed');

    const secondRowText = rows[1].textContent || '';
    expect(secondRowText).toContain('YES_Bank_Passbook.pdf');
    expect(secondRowText).toContain('YES BANK');
    expect(secondRowText).toContain('92 rows');

    const thirdRowText = rows[2].textContent || '';
    expect(thirdRowText).toContain('Axis_Current_Account.pdf');
    expect(thirdRowText).toContain('Axis Bank');
    expect(thirdRowText).toContain('ReadyForProcessing');
  });

  it('4. should render empty state with "No statements yet" and quick action when zero statements exist', () => {
    const emptySummary: DashboardSummary = {
      status: 'Online / Connected',
      database: 'Accufex',
      server: 'SQL Server Connected',
      canConnect: true,
      totalStatements: 0,
      completedStatements: 0,
      statementsProcessed: 0,
      processingStatements: 0,
      failedStatements: 0,
      totalTransactions: 0,
      clientProfiles: 1,
      recentStatements: []
    };
    mockHttp.get.mockReturnValue(of(emptySummary));

    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const emptyState = compiled.querySelector('#dashboard-empty-state');
    expect(emptyState).toBeTruthy();
    expect(emptyState?.textContent).toContain('No statements yet');

    const uploadBtn = compiled.querySelector('#empty-state-upload-btn');
    expect(uploadBtn).toBeTruthy();
    expect(uploadBtn?.getAttribute('routerLink')).toBe('/converter');
  });

  it('5. should show error banner with retry option when API fails', () => {
    mockHttp.get.mockReturnValue(throwError(() => new Error('Server unreachable')));

    fixture.detectChanges();

    expect(component.loading).toBe(false);
    expect(component.errorMessage).toBeTruthy();

    const compiled = fixture.nativeElement as HTMLElement;
    const errorBanner = compiled.querySelector('.error-banner');
    expect(errorBanner).toBeTruthy();
    expect(errorBanner?.textContent).toContain('Unable to connect');

    // Test retry
    mockHttp.get.mockReturnValue(of(sampleSummary));
    const retryBtn = errorBanner?.querySelector('button') as HTMLButtonElement;
    retryBtn.click();
    fixture.detectChanges();

    expect(component.errorMessage).toBeNull();
    expect(component.stats.totalStatements).toBe(4);
  });

  it('6. should delegate download action to StatementService', () => {
    fixture.detectChanges();

    const item = sampleSummary.recentStatements[0];
    component.downloadStatement(item);

    expect(mockStatementService.downloadStatementFile).toHaveBeenCalledWith(
      item.id,
      item.originalFileName
    );
  });

  it('7. should ensure System Connectivity widget and its details are completely absent from DOM', () => {
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;

    expect(compiled.querySelector('.system-status-widget')).toBeNull();
    expect(compiled.querySelector('.status-box-header')).toBeNull();
    expect(compiled.querySelector('.db-live-pip')).toBeNull();

    const text = compiled.textContent || '';
    expect(text).not.toContain('System Connectivity');
    expect(text).not.toContain('Service:');
    expect(text).not.toContain('Data Connected');
  });
});
