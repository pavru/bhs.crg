<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:fn="http://www.w3.org/2005/xpath-functions" xmlns:math="http://www.w3.org/2005/xpath-functions/math" xmlns:array="http://www.w3.org/2005/xpath-functions/array" xmlns:map="http://www.w3.org/2005/xpath-functions/map" xmlns:xhtml="http://www.w3.org/1999/xhtml" xmlns:err="http://www.w3.org/2005/xqt-errors" xmlns:bf="urn:BimHouse:XslFunctions" exclude-result-prefixes="array fn map math xhtml xs err" version="3.0">
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/NewMergeNodesStyles.xsl"/>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>

	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>

	<xsl:variable name="EmptyNode" select="./NonExistingElement"/>

	<xsl:template match="/" name="xsl:initial-template">
		<xsl:copy>
			<xsl:apply-templates select="@*|node()"/>
		</xsl:copy>
	</xsl:template>
	
	<xsl:function name="bf:GetCommonDataFile">
		<xsl:param name="CurrenCommonDataFile"/>
		<xsl:param name="Node"/>
		<xsl:choose>
			<xsl:when test="$Node/node()[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлОбщихДанных')]">
				<xsl:copy-of select="$Node/node()[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлОбщихДанных')][1]"/>
			</xsl:when>
			<xsl:otherwise>
				<xsl:copy-of select="$CurrenCommonDataFile"/>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:function>
	
	<xsl:function name="bf:GetExcludeFilter">
		<xsl:param name="CurrentExcludeFilter"/>
		<xsl:param name="Node"/>
		<xsl:choose>
			<xsl:when test="$Node/ct:Исключения">
				<xsl:element name="ct:Исключения" namespace="urn:BimHouse:CommonDataType">
					<xsl:copy-of select="$CurrentExcludeFilter/*"/>
					<xsl:copy-of select="$Node/ct:Исключения/*"/>
				</xsl:element>
			</xsl:when>
			<xsl:otherwise>
				<xsl:copy-of select="$CurrentExcludeFilter"/>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:function>
	
	<xsl:function name="bf:GetReplacement">
		<xsl:param name="CurrentReplacement"/>
		<xsl:param name="Node"/>
		<xsl:choose>
			<xsl:when test="$Node/ct:Заменить">
				<xsl:element name="ct:Заменить" namespace="urn:BimHouse:CommonDataType">
					<xsl:copy-of select="$CurrentReplacement/*"/>
					<xsl:copy-of select="$Node/ct:Заменить/*"/>
				</xsl:element>
			</xsl:when>
			<xsl:otherwise>
				<xsl:copy-of select="$CurrentReplacement"/>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:function>
	
	<xsl:function name="bf:MustBeExcluded" as="xs:boolean">
		<xsl:param name="ExcludeFilter"/>
		<xsl:param name="Node"/>
		<xsl:variable name="return">
			<xsl:choose>
				<xsl:when test="not($ExcludeFilter) or not($ExcludeFilter/ct:Исключение)"><xsl:value-of select="false()"/></xsl:when>
				<xsl:otherwise>
					<xsl:variable name="test">
						<xsl:for-each select="$ExcludeFilter/ct:Исключение">
							<xsl:variable name="xpath" select="string(@xpath)"/>
							<xsl:variable name="found"><xsl:evaluate xpath="$xpath" context-item="$Node"/></xsl:variable>
							<xsl:if test="count($found/*) > 0">1</xsl:if>
						</xsl:for-each>
					</xsl:variable>
					<xsl:choose>
						<xsl:when test="not(contains(string($test),'1'))"><xsl:value-of select="false()"/></xsl:when>
						<xsl:otherwise><xsl:value-of select="true()"/></xsl:otherwise>
					</xsl:choose>
				</xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
		<xsl:value-of select="$return"/>
	</xsl:function>
	
	<xsl:function name="bf:MustBeRepacedWith">
		<xsl:param name="Replacement"/>
		<xsl:param name="Node"/>
		<xsl:choose>
			<xsl:when test="not($Replacement) or not($Replacement/ct:Элемент)"/>
			<xsl:otherwise>
				<xsl:variable name="NewElement">
					<xsl:for-each select="$Replacement/ct:Элемент">
						<xsl:variable name="xpath" select="string(@xpath)"/>
						<xsl:variable name="found"><xsl:evaluate xpath="$xpath" context-item="$Node"/></xsl:variable>
						<xsl:if test="count($found/*) > 0">
							<xsl:copy-of select="ct:НаЭлемент"/>
						</xsl:if>
					</xsl:for-each>
				</xsl:variable>
				<xsl:if test="count($NewElement/*) > 0">
					<xsl:copy-of select="$NewElement/ct:НаЭлемент[position() = 1]"/>
				</xsl:if>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:function>
	
	
	<xsl:template match="*[node() and not(processing-instruction())]">
		<xsl:variable name="CommonDataFile" select="bf:GetCommonDataFile($EmptyNode,.)"/>
		<xsl:variable name="ExcludeFilter" select="bf:GetExcludeFilter($EmptyNode,.)"/>
		<xsl:variable name="Replacement" select="bf:GetReplacement($EmptyNode,.)"/>
		<xsl:variable name="ResolvedIds" select="fn:string(@id)"/>
		<xsl:variable name="Resolved">
			<xsl:apply-templates select="." mode="resolving">
				<xsl:with-param name="CommonDataFile" select="$CommonDataFile"/>
				<xsl:with-param name="ExcludeFilter" select="$ExcludeFilter"/>
				<xsl:with-param name="Replacement" select="$Replacement"/>
				<xsl:with-param name="ResolvedIds" select="$ResolvedIds"/>
			</xsl:apply-templates>
		</xsl:variable>
		<xsl:variable name="Present">
			<xsl:for-each select="$Resolved">
				<xsl:apply-templates select="." mode="presenting"/>
			</xsl:for-each>
		</xsl:variable>
		<xsl:for-each select="$Present">
			<xsl:apply-templates select="." mode="restore-ns"/>
		</xsl:for-each>
		<!--<xsl:copy-of select="$Present"/>-->
	</xsl:template>
	
	<xsl:template match="@*" mode="resolving excluding replacing">
		<xsl:param name="CommonDataFile"/>
		<xsl:param name="ExcludeFilter"/>
		<xsl:param name="IdStack"/>
		<xsl:copy>
			<xsl:apply-templates select="@*" mode="#current"/>
		</xsl:copy>
	</xsl:template>
	
	<!--<xsl:template match="namespace-node()" mode="#all">
		<xsl:if test="not(./parent::*/parent::*)">
			<xsl:variable name="xmlns" select="string(name())"/>
			<xsl:variable name="uri" select="string($Spaces/*/namespace::*[name() = $xmlns])"/>
			<xsl:if test="starts-with($uri,'urn:BimHouse') and $xmlns != ''">
				<xsl:namespace name="{$xmlns}" select="$uri"/>
			</xsl:if>
		</xsl:if>
	</xsl:template>-->
	
	<xsl:template match="@*|node()" mode="presenting">
		<xsl:copy>
			<xsl:apply-templates select="@*|node()" mode="#current"/>
		</xsl:copy>
	</xsl:template>
	
	<xsl:template match="*" mode="subtype-presenting">
	</xsl:template>

	<xsl:template match="@*|node()" mode="restore-ns">
		<xsl:copy>
			<xsl:if test="node() and not(parent::*)">
				<xsl:for-each select="$Spaces/namespace::*">
					<xsl:variable name="xmlns" select="string(name())"/>
					<xsl:variable name="uri" select="string($Spaces/namespace::*[name() = $xmlns])"/>
					<xsl:if test="starts-with($uri,'urn:BimHouse') and $xmlns != ''">
						<xsl:namespace name="{$xmlns}" select="$uri"/>
					</xsl:if>
				</xsl:for-each>
			</xsl:if>

			<xsl:apply-templates select="@*|node()" mode="#current"/>
		</xsl:copy>
	</xsl:template>
	
	<xsl:template match="node()[not(@ref)]" mode="resolving">
		<xsl:param name="CommonDataFile"/>
		<xsl:param name="ExcludeFilter"/>
		<xsl:param name="Replacement"/>
		<xsl:param name="ResolvedIds"/>
		<xsl:variable name="OwnCommonDataFile" select="bf:GetCommonDataFile($CommonDataFile,.)"/>
		<xsl:variable name="OwnExcludeFilter" select="bf:GetExcludeFilter($ExcludeFilter,.)"/>
		<xsl:variable name="OwnReplacement" select="bf:GetReplacement($Replacement,.)"/>
		<xsl:copy>
			<xsl:apply-templates select="namespace-node()" mode="#current"/>
			<xsl:apply-templates select="@*" mode="#current"/>
			<!--<xsl:apply-templates select="node()[bf:MustBeExcluded($OwnExcludeFilter,.)]" mode="#current">-->
			<xsl:apply-templates select="node()" mode="#current">
				<xsl:with-param name="CommonDataFile" select="$OwnCommonDataFile"/>
				<xsl:with-param name="ExcludeFilter" select="$OwnExcludeFilter"/>
				<xsl:with-param name="Replacement" select="$OwnReplacement"/>
				<xsl:with-param name="ResolvedIds" select="concat(concat($ResolvedIds,' '),@id)"/>
			</xsl:apply-templates>
		</xsl:copy>
	</xsl:template>
	
	<xsl:template match="node()[@ref]" mode="resolving">
		<xsl:param name="CommonDataFile"/>
		<xsl:param name="ExcludeFilter"/>
		<xsl:param name="Replacement"/>
		<xsl:param name="ResolvedIds"/>
		<xsl:param name="UseBaseUri" tunnel="yes" select="fn:base-uri()"/>

		<xsl:variable name="OwnCommonDataFile" select="bf:GetCommonDataFile($CommonDataFile,.)"/>
		<xsl:variable name="OwnExcludeFilter" select="bf:GetExcludeFilter($ExcludeFilter,.)"/>
		<xsl:variable name="OwnReplacement" select="bf:GetReplacement($Replacement,.)"/>
		<xsl:variable name="NewBaseUri">
			<xsl:if test="@uri">
				<!--<xsl:value-of select="base-uri(fn:document(bf:CheckAndCorrectUri(@uri),fn:document($UseBaseUri)))"/>-->
				<!--<xsl:value-of select="fn:resolve-uri(bf:CheckAndCorrectUri(@uri,$UseBaseUri),$UseBaseUri)"/>-->
				<xsl:value-of select="bf:CheckAndCorrectUri(@uri,$UseBaseUri)"/>
			</xsl:if>
		</xsl:variable>
		<xsl:variable name="ExtData">
			<xsl:choose>
				<xsl:when test="@uri">
					<xsl:copy-of select="fn:document(bf:CheckAndCorrectUri(@uri,$UseBaseUri))/*"/>
				</xsl:when>
				<xsl:when test="$OwnCommonDataFile/@uri">
					<xsl:copy-of select="fn:document(bf:CheckAndCorrectUri($OwnCommonDataFile/@uri,$UseBaseUri))/*"/>
					<xsl:copy-of select="/*[not(bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.КаталогОбщихДанных'))]"/>
				</xsl:when>
				<xsl:otherwise>
					<xsl:copy-of select="/*[not(bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.КаталогОбщихДанных'))]"/>
				</xsl:otherwise>
			</xsl:choose>
		</xsl:variable>
		<xsl:variable name="ElementTypeQName" select="resolve-QName(string(@xsi:type),$Spaces)" as="xs:QName"/>
		<xsl:variable name="ElementTypeNamespace" select="string(namespace-uri-from-QName($ElementTypeQName))"/>
		<xsl:variable name="ElementType" select="string(fn:local-name-from-QName($ElementTypeQName))"/>
		<xsl:variable name="ElementId" select="string(@ref)"/>
		
			<xsl:choose>
				<xsl:when test="fn:contains($ResolvedIds, @ref)">
					<xsl:copy>
						<xsl:apply-templates select="namespace-node()" mode="#current"/>
						<xsl:apply-templates select="@*" mode="#current"/>
						<xsl:apply-templates select="node()" mode="#current">
							<xsl:with-param name="CommonDataFile" select="$OwnCommonDataFile"/>
							<xsl:with-param name="Replacement" select="$OwnReplacement"/>
							<xsl:with-param name="ExcludeFilter" select="$OwnExcludeFilter"/>
							<xsl:with-param name="ResolvedIds" select="fn:concat(fn:concat($ResolvedIds,' '),@ref)"/>
						</xsl:apply-templates>
					</xsl:copy>
				</xsl:when>
				<xsl:otherwise>
					<xsl:variable name="Resolved" select="$ExtData//*[bf:SuperTypeOf(.,$ElementTypeNamespace,$ElementType) and @id = $ElementId][1]"/>
					<xsl:if test="not($Resolved)">
						<xsl:message>
							<xsl:text>Элемент </xsl:text>
							<xsl:value-of select="$ElementId"/>
							<xsl:text> с типом </xsl:text>
							<xsl:value-of select="$ElementTypeNamespace"/><xsl:text>:</xsl:text><xsl:value-of select="$ElementType"/>
							<xsl:text> не найден &#xA;</xsl:text>
							<xsl:text>        в файле:</xsl:text><xsl:value-of select="$NewBaseUri"/><xsl:text>&#xA;</xsl:text>
							<xsl:text>        ссылающийся файл:</xsl:text><xsl:value-of select="$UseBaseUri"/>
						</xsl:message>
					</xsl:if>

					<xsl:variable name="Excluded">
						<xsl:for-each select="$Resolved">
							<xsl:call-template name="Exclude">
								<xsl:with-param name="ExcludeFilter" select="$OwnExcludeFilter"/>
							</xsl:call-template>
						</xsl:for-each>
					</xsl:variable>

					<xsl:variable name="Replaced">
						<xsl:for-each select="$Excluded/*">
							<xsl:call-template name="Replace">
								<xsl:with-param name="Replacement" select="$OwnReplacement"/>
							</xsl:call-template>
						</xsl:for-each>
					</xsl:variable>

					<xsl:variable name="Merged">
						<xsl:call-template name="MergeNodes">
							<xsl:with-param name="OrigNode" select="."/>
							<xsl:with-param name="RefNode" select="$Replaced/*"/>
						</xsl:call-template>
					</xsl:variable>
					
					<xsl:variable name="FullyResolved">
						<xsl:for-each select="$Merged">
							<xsl:copy>
								<xsl:apply-templates select="namespace-node()" mode="#current"/>
								<xsl:apply-templates select="@*" mode="#current"/>
								<xsl:apply-templates select="." mode="#current">
									<xsl:with-param name="CommonDataFile" select="$OwnCommonDataFile"/>
									<xsl:with-param name="ExcludeFilter" select="$OwnExcludeFilter"/>
									<xsl:with-param name="Replacement" select="$OwnReplacement"/>
									<xsl:with-param name="ResolvedIds" select="concat(concat($ResolvedIds,' '),@ref)"/>
									<xsl:with-param name="UseBaseUri" select="xs:anyURI($NewBaseUri)" tunnel="yes"/>
								</xsl:apply-templates>
							</xsl:copy>
						</xsl:for-each>
					</xsl:variable>
					<xsl:copy-of select="$FullyResolved/*"/>
				</xsl:otherwise>
			</xsl:choose>
		

	</xsl:template>
	
	<xsl:template name="Exclude" match="node()" mode="excluding">
		<xsl:param name="ExcludeFilter"/>
		<xsl:if test="not(bf:MustBeExcluded($ExcludeFilter,.))">
			<xsl:copy>
				<xsl:apply-templates select="@*|node()" mode="excluding">
					<xsl:with-param name="ExcludeFilter" select="$ExcludeFilter"/>
				</xsl:apply-templates>
			</xsl:copy>
		</xsl:if>
	</xsl:template>

	<xsl:template name="Replace" match="node()" mode="replacing">
		<xsl:param name="Replacement"/>
		<xsl:choose>
			<xsl:when test="bf:MustBeRepacedWith($Replacement,.)">
				<xsl:variable name="NewContent" select="bf:MustBeRepacedWith($Replacement,.)"/>
				<xsl:element name="{name()}" namespace="{namespace-uri()}">
					<xsl:copy-of select="$NewContent/@*"/>
					<xsl:copy-of select="$NewContent/*"/>
				</xsl:element>
			</xsl:when>
			<xsl:otherwise>
				<xsl:copy>
					<xsl:apply-templates select="@*|node()" mode="replacing">
						<xsl:with-param name="Replacement" select="$Replacement"/>
					</xsl:apply-templates>
				</xsl:copy>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>
	
</xsl:stylesheet>
