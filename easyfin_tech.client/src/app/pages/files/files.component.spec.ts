import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NO_ERRORS_SCHEMA, provideZonelessChangeDetection } from '@angular/core';
import { RouterModule } from '@angular/router';
import { By } from '@angular/platform-browser';
import { of, throwError } from 'rxjs';
import { vi, describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FilesComponent } from './files.component';
import { StatementService, StatementSummaryDto, StatementDetailDto } from '../../services/statement.service';

describe('FilesComponent (Phase 12 UI Redesign)', () => {
  let component: FilesComponent;
  let fixture: ComponentFixture<FilesComponent>;
  let mockStatementService: {
    getStatements: ReturnType<typeof vi.fn>;
    getStatementById: ReturnType<typeof vi.fn>;
    downloadStatement: ReturnType<typeof vi.fn>;
    exportExcel: ReturnType<typeof vi.fn>;
    deleteStatement: ReturnType<typeof vi.fn>;
  };

  const sampleStatements: StatementSummaryDto[] = [
    {
      id: 'stmt-001',
      originalFileName: 'HDFC_Bank_Apr2023.pdf',
      fileSizeBytes: 1048576,
      fileSizeFormatted: '1.00 MB',
      uploadedAt: '2023-04-15T10:30:00Z',
      status: 'Completed',
      processingStatus: 2,
      clientId: 'client-1',
      clientName: 'Apex Logistics LLP',
      financialYearId: 'fy-2023',
      financialYearName: '2023-2024'
    },
    {
      id: 'stmt-002',
      originalFileName: 'SBI_Statement_May2023.pdf',
      fileSizeBytes: 524288,
      fileSizeFormatted: '512 KB',
      uploadedAt: '2023-05-10T14:15:00Z',
      status: 'Processing',
      processingStatus: 1,
      clientId: 'client-2',
      clientName: 'Bharat Traders Ltd',
      financialYearId: 'fy-2023',
      financialYearName: '2023-2024'
    },
    {
      id: 'stmt-003',
      originalFileName: 'ICICI_Statement_Jun2023.pdf',
      fileSizeBytes: 2097152,
      fileSizeFormatted: '2.00 MB',
      uploadedAt: '2023-06-20T09:00:00Z',
      status: 'Failed',
      processingStatus: 3,
      clientId: 'client-1',
      clientName: 'Apex Logistics LLP',
      financialYearId: 'fy-2022',
      financialYearName: '2022-2023'
    },
    {
      id: 'stmt-004',
      originalFileName: 'Axis_Statement_Jul2023.pdf',
      fileSizeBytes: 262144,
      fileSizeFormatted: '256 KB',
      uploadedAt: '2023-07-01T11:00:00Z',
      status: 'Ready',
      processingStatus: 0,
      clientId: 'client-none',
      financialYearId: 'fy-none'
    }
  ];

  const sampleDetail: StatementDetailDto = {
    id: 'stmt-001',
    originalFileName: 'HDFC_Bank_Apr2023.pdf',
    storedFileName: 'stored_hdfc_001.pdf',
    extension: '.pdf',
    contentType: 'application/pdf',
    fileSizeBytes: 1048576,
    fileSizeFormatted: '1.00 MB',
    uploadedAt: '2023-04-15T10:30:00Z',
    status: 'Completed',
    processingStatus: 2,
    clientId: 'client-1',
    clientName: 'Apex Logistics LLP',
    financialYearId: 'fy-2023',
    financialYearName: '2023-2024'
  };

  beforeEach(async () => {
    mockStatementService = {
      getStatements: vi.fn().mockReturnValue(of(sampleStatements)),
      getStatementById: vi.fn().mockReturnValue(of(sampleDetail)),
      downloadStatement: vi.fn().mockReturnValue(of(new Blob(['fake pdf content'], { type: 'application/pdf' }))),
      exportExcel: vi.fn().mockReturnValue(of(new Blob(['fake excel content'], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))),
      deleteStatement: vi.fn().mockReturnValue(of(undefined))
    };

    await TestBed.configureTestingModule({
      declarations: [FilesComponent],
      imports: [CommonModule, FormsModule, RouterModule.forRoot([])],
      providers: [
        provideZonelessChangeDetection(),
        { provide: StatementService, useValue: mockStatementService }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(FilesComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('1. should create component and initialize default state', () => {
    expect(component).toBeTruthy();
    expect(component.statements).toEqual([]);
    expect(component.isLoading).toBe(true);
    expect(component.errorMessage).toBeNull();
    expect(component.searchQuery).toBe('');
    expect(component.selectedClientFilter).toBe('ALL');
    expect(component.selectedFyFilter).toBe('ALL');
    expect(component.selectedStatusFilter).toBe('ALL');
    expect(component.sortBy).toBe('newest');
  });

  it('2. should load statements on ngOnInit and populate KPI metrics', () => {
    fixture.detectChanges();

    expect(mockStatementService.getStatements).toHaveBeenCalledTimes(1);
    expect(component.isLoading).toBe(false);
    expect(component.statements.length).toBe(4);

    // Verify KPI calculations
    expect(component.totalStatementsCount).toBe(4);
    expect(component.completedStatementsCount).toBe(1); // stmt-001 (status 2)
    expect(component.inProgressStatementsCount).toBe(2); // stmt-002 (status 1) and stmt-004 (status 0)
    expect(component.failedStatementsCount).toBe(1); // stmt-003 (status 3)
  });

  it('3. should correctly extract unique availableClients and availableFinancialYears', () => {
    fixture.detectChanges();

    // Clients sorted alphabetically, fallback to 'Default Workspace' when undefined
    expect(component.availableClients).toEqual(['Apex Logistics LLP', 'Bharat Traders Ltd', 'Default Workspace']);

    // FYs sorted in descending order
    expect(component.availableFinancialYears).toEqual(['2023-2024', '2022-2023']);
  });

  it('4. should handle error when loading statements fails', () => {
    mockStatementService.getStatements.mockReturnValue(throwError(() => new Error('Network error')));
    fixture.detectChanges();

    expect(component.isLoading).toBe(false);
    expect(component.statements.length).toBe(0);
    expect(component.errorMessage).toContain('Failed to load statements from database');

    const errorEl = fixture.debugElement.query(By.css('.workspace-error-state'));
    expect(errorEl).toBeTruthy();
  });

  describe('Filtering and Sorting Pipeline', () => {
    beforeEach(() => {
      fixture.detectChanges();
    });

    it('5. should filter by text search query across filename, client, FY, or ID', () => {
      // Filename search
      component.searchQuery = 'HDFC';
      expect(component.filteredStatements.length).toBe(1);
      expect(component.filteredStatements[0].id).toBe('stmt-001');

      // Client name search
      component.searchQuery = 'Bharat';
      expect(component.filteredStatements.length).toBe(1);
      expect(component.filteredStatements[0].id).toBe('stmt-002');

      // ID search
      component.searchQuery = 'stmt-003';
      expect(component.filteredStatements.length).toBe(1);
      expect(component.filteredStatements[0].id).toBe('stmt-003');

      // Non-matching search
      component.searchQuery = 'NonExistentBank';
      expect(component.filteredStatements.length).toBe(0);
    });

    it('6. should filter by selected client', () => {
      component.selectedClientFilter = 'Apex Logistics LLP';
      const results = component.filteredStatements;
      expect(results.length).toBe(2);
      expect(results.every(s => s.clientName === 'Apex Logistics LLP')).toBe(true);

      component.selectedClientFilter = 'Default Workspace';
      const defaultResults = component.filteredStatements;
      expect(defaultResults.length).toBe(1);
      expect(defaultResults[0].id).toBe('stmt-004');
    });

    it('7. should filter by financial year', () => {
      component.selectedFyFilter = '2023-2024';
      const results = component.filteredStatements;
      expect(results.length).toBe(2);
      expect(results.map(s => s.id)).toEqual(['stmt-002', 'stmt-001']); // sorted newest first by default

      component.selectedFyFilter = '2022-2023';
      expect(component.filteredStatements.length).toBe(1);
      expect(component.filteredStatements[0].id).toBe('stmt-003');
    });

    it('8. should filter by processing status', () => {
      component.selectedStatusFilter = 'COMPLETED';
      expect(component.filteredStatements.length).toBe(1);
      expect(component.filteredStatements[0].id).toBe('stmt-001');

      component.selectedStatusFilter = 'PROCESSING';
      expect(component.filteredStatements.length).toBe(2);
      expect(component.filteredStatements.map(s => s.id)).toEqual(['stmt-004', 'stmt-002']);

      component.selectedStatusFilter = 'FAILED';
      expect(component.filteredStatements.length).toBe(1);
      expect(component.filteredStatements[0].id).toBe('stmt-003');
    });

    it('9. should sort records correctly by newest, oldest, name, and size', () => {
      // Newest first (default)
      component.sortBy = 'newest';
      expect(component.filteredStatements.map(s => s.id)).toEqual([
        'stmt-004', // 2023-07-01
        'stmt-003', // 2023-06-20
        'stmt-002', // 2023-05-10
        'stmt-001'  // 2023-04-15
      ]);

      // Oldest first
      component.sortBy = 'oldest';
      expect(component.filteredStatements.map(s => s.id)).toEqual([
        'stmt-001',
        'stmt-002',
        'stmt-003',
        'stmt-004'
      ]);

      // Alphabetical filename (A-Z)
      component.sortBy = 'name';
      expect(component.filteredStatements.map(s => s.originalFileName)).toEqual([
        'Axis_Statement_Jul2023.pdf',
        'HDFC_Bank_Apr2023.pdf',
        'ICICI_Statement_Jun2023.pdf',
        'SBI_Statement_May2023.pdf'
      ]);

      // Size descending (largest first)
      component.sortBy = 'size';
      expect(component.filteredStatements.map(s => s.fileSizeBytes)).toEqual([
        2097152, // 2 MB (ICICI)
        1048576, // 1 MB (HDFC)
        524288,  // 512 KB (SBI)
        262144   // 256 KB (Axis)
      ]);
    });

    it('10. should report hasActiveFilters and clearFilters properly', () => {
      expect(component.hasActiveFilters).toBe(false);

      component.searchQuery = 'test';
      expect(component.hasActiveFilters).toBe(true);

      component.clearFilters();
      expect(component.searchQuery).toBe('');
      expect(component.selectedClientFilter).toBe('ALL');
      expect(component.selectedFyFilter).toBe('ALL');
      expect(component.selectedStatusFilter).toBe('ALL');
      expect(component.sortBy).toBe('newest');
      expect(component.hasActiveFilters).toBe(false);
    });
  });

  describe('Statement Actions (Download, Excel, Ingestion Details)', () => {
    beforeEach(() => {
      fixture.detectChanges();
    });

    it('11. should trigger PDF download when downloadStatement is called', () => {
      const createObjectURLSpy = vi.spyOn(window.URL, 'createObjectURL').mockReturnValue('blob:mock-url');
      const revokeObjectURLSpy = vi.spyOn(window.URL, 'revokeObjectURL').mockImplementation(() => {});

      const item = sampleStatements[0];
      component.downloadStatement(item);

      expect(mockStatementService.downloadStatement).toHaveBeenCalledWith('stmt-001');
      expect(createObjectURLSpy).toHaveBeenCalled();
      expect(revokeObjectURLSpy).toHaveBeenCalledWith('blob:mock-url');
      expect(component.isDownloading['stmt-001']).toBe(false);
    });

    it('12. should trigger ClosedXML Excel export for completed statements', () => {
      const createObjectURLSpy = vi.spyOn(window.URL, 'createObjectURL').mockReturnValue('blob:mock-url');
      const revokeObjectURLSpy = vi.spyOn(window.URL, 'revokeObjectURL').mockImplementation(() => {});

      const item = sampleStatements[0]; // status 2 (completed)
      component.exportExcel(item);

      expect(mockStatementService.exportExcel).toHaveBeenCalledWith('stmt-001');
      expect(createObjectURLSpy).toHaveBeenCalled();
      expect(revokeObjectURLSpy).toHaveBeenCalledWith('blob:mock-url');
      expect(component.isExporting['stmt-001']).toBe(false);
    });

    it('13. should NOT trigger Excel export if processingStatus is not 2', () => {
      const inProgressItem = sampleStatements[1]; // status 1 (Processing)
      component.exportExcel(inProgressItem);

      expect(mockStatementService.exportExcel).not.toHaveBeenCalled();
    });

    it('14. should open, view, and close statement ingestion details modal', () => {
      component.viewDetails('stmt-001');

      expect(mockStatementService.getStatementById).toHaveBeenCalledWith('stmt-001');
      expect(component.selectedDetail).toEqual(sampleDetail);
      expect(component.isLoadingDetail).toBe(false);

      // Close modal
      component.closeDetails();
      expect(component.selectedDetail).toBeNull();
    });

    it('15. should handle error when viewing statement details fails', () => {
      mockStatementService.getStatementById.mockReturnValue(throwError(() => new Error('Not found')));
      component.viewDetails('stmt-999');

      expect(component.isLoadingDetail).toBe(false);
      expect(component.detailError).toBe('Could not load statement details.');
      expect(component.selectedDetail).toBeNull();
    });

    it('16. should copy statement ID to clipboard in detail modal', () => {
      component.selectedDetail = sampleDetail;
      const writeTextMock = vi.fn().mockReturnValue(Promise.resolve());
      Object.assign(navigator, {
        clipboard: {
          writeText: writeTextMock
        }
      });

      component.copyDetailId();
      expect(writeTextMock).toHaveBeenCalledWith('stmt-001');
      expect(component.detailIdCopied).toBe(true);
    });
  });

  describe('DOM Template and Empty States', () => {
    it('17. should render KPI cards with correct counts in the DOM', () => {
      fixture.detectChanges();

      const totalKpiEl = fixture.debugElement.query(By.css('#kpiTotalStatements .kpi-value'));
      const completedKpiEl = fixture.debugElement.query(By.css('#kpiCompletedStatements .kpi-value'));
      const processingKpiEl = fixture.debugElement.query(By.css('#kpiProcessingStatements .kpi-value'));
      const failedKpiEl = fixture.debugElement.query(By.css('#kpiFailedStatements .kpi-value'));

      expect(totalKpiEl.nativeElement.textContent.trim()).toBe('4');
      expect(completedKpiEl.nativeElement.textContent.trim()).toBe('1');
      expect(processingKpiEl.nativeElement.textContent.trim()).toBe('2');
      expect(failedKpiEl.nativeElement.textContent.trim()).toBe('1');
    });

    it('18. should display table rows with filenames, badges, and action buttons', () => {
      fixture.detectChanges();

      const rows = fixture.debugElement.queryAll(By.css('tbody tr'));
      expect(rows.length).toBe(4);

      // Verify Excel button exists on completed row but not on processing row
      const excelBtnCompleted = fixture.debugElement.query(By.css('#exportExcelBtn-stmt-001'));
      expect(excelBtnCompleted).toBeTruthy();

      const excelBtnProcessing = fixture.debugElement.query(By.css('#exportExcelBtn-stmt-002'));
      expect(excelBtnProcessing).toBeNull();
    });

    it('19. should show filter empty state when query returns no results', () => {
      fixture.detectChanges();

      component.searchQuery = 'NonExistentFile';
      component.cdr.markForCheck();
      fixture.detectChanges();

      const filterEmpty = fixture.debugElement.query(By.css('#filterEmptyState'));
      expect(filterEmpty).toBeTruthy();
      expect(filterEmpty.nativeElement.textContent).toContain('No matching statements found');
    });

    it('20. should show zero records empty state when database returns 0 statements', () => {
      mockStatementService.getStatements.mockReturnValue(of([]));
      fixture.detectChanges();

      expect(component.statements.length).toBe(0);
      const zeroEmpty = fixture.debugElement.query(By.css('#zeroStatementsEmptyState'));
      expect(zeroEmpty).toBeTruthy();
      expect(zeroEmpty.nativeElement.textContent).toContain('No Statements Processed Yet');
    });

    it('21. should render delete button in DOM and call deleteStatement upon confirmation', () => {
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      fixture.detectChanges();

      const deleteBtn = fixture.debugElement.query(By.css('#deleteBtn-stmt-001'));
      expect(deleteBtn).toBeTruthy();

      deleteBtn.nativeElement.click();
      expect(mockStatementService.deleteStatement).toHaveBeenCalledWith('stmt-001');
      expect(component.statements.some(s => s.id === 'stmt-001')).toBe(false);
    });

    it('22. should not call deleteStatement if user cancels confirmation', () => {
      vi.spyOn(window, 'confirm').mockReturnValue(false);
      fixture.detectChanges();

      const deleteBtn = fixture.debugElement.query(By.css('#deleteBtn-stmt-001'));
      deleteBtn.nativeElement.click();

      expect(mockStatementService.deleteStatement).not.toHaveBeenCalled();
      expect(component.statements.some(s => s.id === 'stmt-001')).toBe(true);
    });
  });
});
